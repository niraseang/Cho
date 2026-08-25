"""
Policy/value network for Cho.

Two policy heads rather than one, because the game has two action spaces:

  chess head         from x to over squares. AlphaZero-chess's ray-based encoding cannot
                     be used here - the chain rule lets a rook turn a corner, so a
                     destination is not reachable along a ray from its origin.
  intersection head  shared by main stones, territory removals and pawn bonus stones.
                     All three are "which intersection", and the phase planes disambiguate.

The trunk is deliberately small. On a 6x7 board a 3x3 convolution covers the whole board
after about three blocks, so extra depth buys refinement, not reach.
"""

import torch
import torch.nn as nn
import torch.nn.functional as F


class ResidualBlock(nn.Module):
    def __init__(self, channels):
        super().__init__()
        self.c1 = nn.Conv2d(channels, channels, 3, padding=1, bias=False)
        self.b1 = nn.BatchNorm2d(channels)
        self.c2 = nn.Conv2d(channels, channels, 3, padding=1, bias=False)
        self.b2 = nn.BatchNorm2d(channels)

    def forward(self, x):
        y = F.relu(self.b1(self.c1(x)))
        y = self.b2(self.c2(y))
        return F.relu(x + y)


class ChoNet(nn.Module):
    def __init__(self, planes, height, width, board_w, board_h,
                 channels=64, blocks=4):
        super().__init__()
        self.height, self.width = height, width
        self.chess_size = (board_w * board_h) ** 2
        self.inter_size = height * width

        self.stem = nn.Sequential(
            nn.Conv2d(planes, channels, 3, padding=1, bias=False),
            nn.BatchNorm2d(channels),
            nn.ReLU(inplace=True),
        )
        self.trunk = nn.Sequential(*[ResidualBlock(channels) for _ in range(blocks)])

        # Chess policy, kept spatial. One output channel per DESTINATION square, read at the
        # cell of the ORIGIN square, so logit[from, to] falls out of a 1x1 convolution.
        # A dense Linear(32*H*W -> 900) would work but costs 1.2M parameters - four times the
        # rest of the network - and throws away the fact that `from` is a board location.
        self.squares = board_w * board_h
        self.board_w, self.board_h = board_w, board_h
        self.pc = nn.Conv2d(channels, self.squares, 1)

        self.promo = nn.Sequential(
            nn.Conv2d(channels, 4, 1),
            nn.AdaptiveAvgPool2d(1),
            nn.Flatten(),
        )

        # intersection policy: one logit per cell, so it stays spatial
        self.pi = nn.Sequential(
            nn.Conv2d(channels, 1, 1),
            nn.Flatten(),
        )

        # value
        self.v = nn.Sequential(
            nn.Conv2d(channels, 8, 1, bias=False),
            nn.BatchNorm2d(8),
            nn.ReLU(inplace=True),
            nn.Flatten(),
            nn.Linear(8 * height * width, 64),
            nn.ReLU(inplace=True),
            nn.Linear(64, 1),
            nn.Tanh(),
        )

    def forward(self, x):
        h = self.trunk(self.stem(x))
        b = h.shape[0]

        # (B, to, H, W) -> crop to the square sub-grid -> (B, from, to) -> (B, from*to)
        chess = self.pc(h)[:, :, :self.board_h, :self.board_w]
        chess = chess.permute(0, 2, 3, 1).reshape(b, self.squares * self.squares)

        return {
            "chess": chess,
            "promo": self.promo(h),
            "inter": self.pi(h),
            "value": self.v(h).squeeze(-1),
        }

    def parameter_count(self):
        return sum(p.numel() for p in self.parameters())


def masked_policy_loss(out, is_chess, idx, target, mask):
    """
    Cross-entropy against the search's visit distribution, over exactly the legal moves.

    The two heads have different widths, so the narrower one is padded with -inf before
    selection; padding entries can never win the softmax.

    Returns (loss, entropy_floor). The loss cannot reach zero - the target is a
    distribution, not a label - so the floor is what a perfect fit would score.
    """
    chess, inter = out["chess"], out["inter"]
    b = chess.shape[0]

    pad = chess.new_full((b, chess.shape[1] - inter.shape[1]), float("-inf"))
    inter_padded = torch.cat([inter, pad], dim=1)

    logits = torch.where(is_chess.unsqueeze(1), chess, inter_padded)
    gathered = logits.gather(1, idx).masked_fill(~mask, float("-inf"))

    logp = F.log_softmax(gathered, dim=1)
    loss = -(target * logp.nan_to_num(neginf=0.0)).sum(dim=1).mean()

    safe = target.clamp_min(1e-9)
    entropy = -(target * safe.log()).sum(dim=1).mean()
    return loss, entropy
