using System.Collections.Generic;
using UnityEngine;

public class GameController : MonoBehaviour
{
    public enum PlayMode
    {
        HumanVsHuman,
        HumanVsAI,
        AIVsAI
    }

    [Header("Play mode / AI control")]
    public PlayMode playMode = PlayMode.HumanVsHuman;

    [Tooltip("In HumanVsAI mode, which side is the human player.")]
    public PieceColor humanSide = PieceColor.White;

    [Tooltip("When true, White is controlled by the built-in AI.")]
    public bool whiteIsAI = false;

    [Tooltip("When true, Black is controlled by the built-in AI.")]
    public bool blackIsAI = false;

    [Header("AI search tuning")]
    [Tooltip("Maximum ply depth for phase-one (chess) AI search.")]
    public int aiMaxDepth = 4;

    [Tooltip("Time budget (ms) for phase-one (chess) AI search.")]
    public int aiTimeBudgetMs = 1000;

    [Header("AI evaluation toggles")]
    [Tooltip("If enabled, adds a small mobility term based on (white legal moves - black legal moves). Territory only matters via mobility.")]
    public bool aiUseMobilityEval = true;

    [Tooltip("Weight per move of mobility advantage. Keep small so material dominates (typical 1-5).")]
    public int aiMobilityWeight = 2;

    [Header("Runtime debug controls")]
    [Tooltip("Allow toggling AI control at runtime via hotkeys.")]
    public bool allowRuntimeAiToggles = true;

    [Tooltip("Hotkey to toggle White AI.")]
    public KeyCode toggleWhiteAiKey = KeyCode.F1;

    [Tooltip("Hotkey to toggle Black AI.")]
    public KeyCode toggleBlackAiKey = KeyCode.F2;

    [Tooltip("Hotkey to toggle both AIs.")]
    public KeyCode toggleBothAiKey = KeyCode.F3;

    [Header("Move history (debug)")]
    [Tooltip("When enabled, logs the last chess move and a copy/paste move chain string after each chess move.")]
    public bool logChessMoveChain = true;

    [Tooltip("When enabled, logs when the AI skips an immediate queen capture available in the sim move list.")]
    public bool aiLogMissedQueenCaptures = false;

    private readonly List<string> _chessMoveHistory = new List<string>();

    // Debug: capture Go deltas associated with the chess move that started the turn.
    // We snapshot Go stones immediately after the chess move, then diff at EndTurn.
    private string _pendingChessMoveToken = null;
    private sbyte[,] _goSnapshotAfterChess = null; // -1 none, 0 white, 1 black

    public BoardManager boardManager;
    public RulesEngine rulesEngine;
    public PieceColor currentPlayer = PieceColor.White;
    bool phaseOne = true;

    public GameObject whiteStonePrefab;
    public GameObject blackStonePrefab;

    Piece selectedPiece;
    List<Vector2Int> legalMoves = new List<Vector2Int>();

    // NEW: state for post-move enemy-stone removal
    bool waitingForTerritoryClick = false;
    Vector2Int lastMovedSquare;

    // NEW: state for pawn extra stone choice
    bool waitingForPawnStoneChoice = false;
    List<Intersection> pendingPawnCornerOptions = new List<Intersection>();

    // NEW: track last move from-square for highlight
    Vector2Int? lastMoveFromSquare = null;

    // NEW: orientation flag (false = white at bottom, true = black at bottom)
    public bool blackPerspective = false;

    // NEW: one-time black initial stone placement before White's first move
    bool blackInitialStonePending = true;

    [Header("Game state")]
    public bool gameOver = false;
    public PieceColor winner = PieceColor.White;

    // Simple-ko: intersection forbidden for the NEXT normal Go move (phase-two main placement).
    // This applies to normal phase-two placements and the initial black stone,
    // but NOT to extra pawn bonus stone placements.
    public Vector2Int? goKoPoint = null;

    // Board positions already reached this game, for the superko restriction.
    // Shared with the AI through SimStateBuilder so its root move list is filtered too.
    public SimPositionHistory PositionHistory { get; } = new SimPositionHistory();

    // Track previous values so we can detect runtime changes in Update
    private PlayMode _lastPlayMode;
    private bool _lastWhiteIsAI;
    private bool _lastBlackIsAI;

    void Start()
    {
        // Cache initial values so we can detect changes later
        _lastPlayMode = playMode;

        // Sync AI flags from playMode at startup.
        ApplyPlayModeToAIFlags();

        // Cache after applying playMode so these match the actual runtime flags.
        _lastWhiteIsAI = whiteIsAI;
        _lastBlackIsAI = blackIsAI;

        if (boardManager == null) boardManager = Object.FindFirstObjectByType<BoardManager>();
        if (rulesEngine == null) rulesEngine = Object.FindFirstObjectByType<RulesEngine>();
        whiteStonePrefab = boardManager.whiteStonePrefab;
        blackStonePrefab = boardManager.blackStonePrefab;

        if (boardManager == null)
        {
            Debug.LogError("GameController: BoardManager is null.");
            return;
        }
        if (boardManager.piecePrefab == null)
        {
            Debug.LogError("GameController: BoardManager.piecePrefab is not assigned.");
        }

        _chessMoveHistory.Clear();
        boardManager.SetupInitialPieces();
        RefreshAiButtonVisual();

        PositionHistory.Clear();
        RecordPositionForSuperko();

        // If Black is AI at start and the special stone is pending, place it immediately.
        if (blackIsAI && blackInitialStonePending)
        {
            RunAiBlackInitialStonePlacement();
        }
    }

    // Helper to sync AI flags from playMode when playMode changes
    void ApplyPlayModeToAIFlags()
    {
        switch (playMode)
        {
            case PlayMode.HumanVsHuman:
                whiteIsAI = false;
                blackIsAI = false;
                break;
            case PlayMode.HumanVsAI:
                // Allow either side to be the human player.
                // If human plays White -> Black is AI. If human plays Black -> White is AI.
                if (humanSide == PieceColor.White)
                {
                    whiteIsAI = false;
                    blackIsAI = true;
                }
                else
                {
                    whiteIsAI = true;
                    blackIsAI = false;
                }
                break;
            case PlayMode.AIVsAI:
                whiteIsAI = true;
                blackIsAI = true;
                break;
        }
    }

    // LEGACY INPUT: poll mouse in Update
    void Update()
    {
        if (gameOver) return;

        // Runtime AI toggles (useful for setting up positions in HumanVsHuman).
        if (allowRuntimeAiToggles)
        {
            if (Input.GetKeyDown(toggleWhiteAiKey))
            {
                whiteIsAI = !whiteIsAI;
            }
            if (Input.GetKeyDown(toggleBlackAiKey))
            {
                blackIsAI = !blackIsAI;
            }
            if (Input.GetKeyDown(toggleBothAiKey))
            {
                bool newValue = !(whiteIsAI && blackIsAI);
                whiteIsAI = newValue;
                blackIsAI = newValue;
            }
        }

        // Detect runtime changes from the Inspector and react
        if (playMode != _lastPlayMode)
        {
            bool prevBlackAI = _lastBlackIsAI;

            _lastPlayMode = playMode;
            ApplyPlayModeToAIFlags();

            // If Black AI was just enabled and the initial stone is still pending, place it now.
            if (!prevBlackAI && blackIsAI && blackInitialStonePending)
            {
                RunAiBlackInitialStonePlacement();
            }

            // Keep cached flags in sync with playMode-driven values.
            _lastWhiteIsAI = whiteIsAI;
            _lastBlackIsAI = blackIsAI;

            RefreshAiButtonVisual();
        }

        if (whiteIsAI != _lastWhiteIsAI || blackIsAI != _lastBlackIsAI)
        {
            bool prevBlackAI = _lastBlackIsAI;
            _lastWhiteIsAI = whiteIsAI;
            _lastBlackIsAI = blackIsAI;

            // If Black AI has just been turned on while its initial stone is still pending, place it now.
            if (!prevBlackAI && blackIsAI && blackInitialStonePending)
            {
                RunAiBlackInitialStonePlacement();
            }

            RefreshAiButtonVisual();
        }

        // IMPORTANT: before White's first move, Black must place one initial Go stone.
        // During this pending state, the only "AI turn" that can occur is Black's AI
        // placing that initial stone. White AI must NOT move until the stone is placed.
        bool aiTurn = IsAIToMove();
        if (blackInitialStonePending)
        {
            aiTurn = blackIsAI;
        }

        // Always allow mouse clicks (for flip button and human moves).
        if (Input.GetMouseButtonDown(0))
        {
            // First check if we clicked the flip button
            if (TryHandleFlipButtonClick_Legacy())
                return;

            // Then the in-game AI on/off button
            if (TryHandleAiToggleButtonClick_Legacy())
                return;

            // Special case: while the initial black Go stone is still pending,
            // allow Black (human) to place it by click even if it's currently an AI turn
            // (eg. because White is AI).
            if (blackInitialStonePending && !blackIsAI)
            {
                HandleBoardClick_Legacy();
                return;
            }

            // Only process board clicks when it's a human player's turn
            if (!aiTurn)
            {
                HandleBoardClick_Legacy();
                return;
            }
        }

        // After processing any input for this frame, let the AI act if it's its turn.
        if (aiTurn)
        {
            RunAiTurn();
        }
    }

    bool TryHandleFlipButtonClick_Legacy()
    {
        if (boardManager == null || boardManager.flipButtonObject == null)
            return false;

        Camera cam = Camera.main;
        if (cam == null) return false;

        Vector3 worldPos = cam.ScreenToWorldPoint(Input.mousePosition);
        Vector2 worldPos2D = new Vector2(worldPos.x, worldPos.y);

        RaycastHit2D hit = Physics2D.Raycast(worldPos2D, Vector2.zero);
        if (hit.collider == null)
        {
            return false;
        }

        if (hit.collider.gameObject == boardManager.flipButtonObject)
        {
            boardManager.FlipBoardVisual();
            return true;
        }

        return false;
    }

    bool TryHandleAiToggleButtonClick_Legacy()
    {
        if (boardManager == null || boardManager.aiButtonObject == null)
            return false;

        Camera cam = Camera.main;
        if (cam == null) return false;

        Vector3 worldPos = cam.ScreenToWorldPoint(Input.mousePosition);
        Vector2 worldPos2D = new Vector2(worldPos.x, worldPos.y);

        RaycastHit2D hit = Physics2D.Raycast(worldPos2D, Vector2.zero);
        if (hit.collider == null)
        {
            return false;
        }

        if (hit.collider.gameObject == boardManager.aiButtonObject)
        {
            CycleAiMode();
            return true;
        }

        return false;
    }

    // Step the AI setting during play: off -> Black -> White -> both -> off.
    // Also usable from a uGUI Button's OnClick.
    public void CycleAiMode()
    {
        if (!whiteIsAI && !blackIsAI)
        {
            // off -> Black is AI (human keeps White)
            playMode = PlayMode.HumanVsAI;
            humanSide = PieceColor.White;
        }
        else if (blackIsAI && !whiteIsAI)
        {
            // Black -> White is AI (human takes Black)
            playMode = PlayMode.HumanVsAI;
            humanSide = PieceColor.Black;
        }
        else if (whiteIsAI && !blackIsAI)
        {
            // White -> both sides AI
            playMode = PlayMode.AIVsAI;
        }
        else
        {
            // both -> off
            playMode = PlayMode.HumanVsHuman;
        }

        // Apply immediately rather than waiting for Update's playMode change-detection,
        // which would miss a repeat of the same playMode (eg. HumanVsAI with humanSide
        // flipped) and would also miss flags changed by hotkey.
        _lastPlayMode = playMode;
        ApplyPlayModeToAIFlags();
        RefreshAiButtonVisual();

        // _lastWhiteIsAI/_lastBlackIsAI are deliberately left stale: Update's flag-change
        // block picks them up and places Black's pending opening stone if the AI just took
        // that side.
        Debug.Log($"[GameController] AI white={whiteIsAI} black={blackIsAI} (human plays {humanSide})");
    }

    void RefreshAiButtonVisual()
    {
        if (boardManager == null) return;
        boardManager.SetAiButtonState(whiteIsAI, blackIsAI);
    }

    // Original legacy click handler: reads mouse position directly
    void HandleBoardClick_Legacy()
    {
        if (boardManager == null) return;
        if (gameOver) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 worldPos = cam.ScreenToWorldPoint(Input.mousePosition);

        // Before any White chess move, give Black a single initial stone placement.
        if (blackInitialStonePending)
        {
            HandleBlackInitialStonePlacement(worldPos);
            return;
        }

        HandleBoardClick_Legacy(worldPos);
    }

    // New overload used by AI, takes a world position directly
    void HandleBoardClick_Legacy(Vector2 worldPos)
    {
        if (boardManager == null) return;
        if (gameOver) return;

        if (phaseOne)
        {
            var sq = WorldToSquare(worldPos);
            if (!sq.HasValue) return;
            Vector2Int coord = sq.Value;
            var piece = boardManager.GetSquarePiece(coord);

            if (selectedPiece == null)
            {
                if (piece != null && piece.color == currentPlayer)
                {
                    selectedPiece = piece;
                    legalMoves = GenerateExtendedReachMoves(selectedPiece);
                    boardManager.HighlightSquares(legalMoves);
                }
            }
            else
            {
                if (legalMoves.Contains(coord))
                {
                    ExecuteMove(selectedPiece, coord);
                    selectedPiece = null;
                    legalMoves.Clear();
                    boardManager.ClearHighlights();
                    phaseOne = false; // always end phase one after an extended move
                }
                else
                {
                    if (piece != null && piece.color == currentPlayer)
                    {
                        selectedPiece = piece;
                        legalMoves = GenerateExtendedReachMoves(selectedPiece);
                        boardManager.HighlightSquares(legalMoves);
                    }
                    else
                    {
                        selectedPiece = null;
                        legalMoves.Clear();
                        boardManager.ClearHighlights();
                    }
                }
            }
        }
        else
        {
            // If we just entered enemy territory and must remove one enemy stone by click
            if (waitingForTerritoryClick)
            {
                HandleTerritoryRemovalClick(worldPos);
                return;
            }

            // If we are choosing where a pawn's extra stone goes
            if (waitingForPawnStoneChoice)
            {
                HandlePawnExtraStoneChoiceClick(worldPos);
                return;
            }

            // Normal Phase Two stone placement
            var nearest = FindNearestIntersection(worldPos);
            if (nearest != null && nearest.occupant == null)
            {
                // Ko rule: normal Go moves may not play on the ko point.
                if (goKoPoint.HasValue && nearest.x == goKoPoint.Value.x && nearest.y == goKoPoint.Value.y)
                {
                    return;
                }

                // Superko: this placement may not recreate a position already seen this game.
                if (ViolatesSuperko(nearest.x, nearest.y))
                {
                    return;
                }

                bool placed = PlaceStoneSafe(nearest.x, nearest.y, currentPlayer == PieceColor.White ? StoneColor.White : StoneColor.Black);
                if (placed)
                {
                    // First resolve captures of adjacent enemy groups
                    var captured = rulesEngine.ResolveCapturesAfterPlacement(nearest.x, nearest.y);
                    foreach (var s in captured) RemoveStone(s);

                    // Surround capture resolves while the placed stone is still on the board, so a
                    // stone about to die by suicide can still complete a four-corner surround.
                    CheckAllPiecesForSurroundCapture();

                    // Suicide is legal: the move stands and ends the turn, but the group dies.
                    bool isSuicide = captured.Count == 0 && !rulesEngine.HasLiberties(nearest.x, nearest.y);

                    // Update ko point for the next normal Go move.
                    // Simple ko condition: captured exactly one stone, and the placed stone is a single-stone group
                    // with exactly one liberty after the capture.
                    Vector2Int? nextKo = null;
                    if (!isSuicide && captured.Count == 1)
                    {
                        var placedGroup = rulesEngine.GetGroupStones(nearest.x, nearest.y);
                        if (placedGroup.Count == 1)
                        {
                            int libs = rulesEngine.CountLiberties(nearest.x, nearest.y);
                            if (libs == 1)
                            {
                                nextKo = new Vector2Int(captured[0].ix, captured[0].iy);
                            }
                        }
                    }
                    goKoPoint = nextKo;

                    if (isSuicide)
                    {
                        var selfGroup = rulesEngine.GetGroupStones(nearest.x, nearest.y);
                        foreach (var s in selfGroup) RemoveStone(s);
                    }

                    EndTurn();
                }
            }
        }
    }

    void HandleBlackInitialStonePlacement(Vector2 worldPos)
    {
        var nearest = FindNearestIntersection(worldPos);
        if (nearest == null || nearest.occupant != null) return;

        // Ko rule: initial black stone is a normal Go placement, so ko applies here too.
        if (goKoPoint.HasValue && nearest.x == goKoPoint.Value.x && nearest.y == goKoPoint.Value.y)
        {
            return;
        }

        bool placed = PlaceStoneSafe(nearest.x, nearest.y, StoneColor.Black);
        if (!placed) return;

        // Resolve captures caused by this initial stone
        var captured = rulesEngine.ResolveCapturesAfterPlacement(nearest.x, nearest.y);
        foreach (var s in captured) RemoveStone(s);

        // Surround resolves before the stone can die.
        CheckAllPiecesForSurroundCapture();

        // Suicide is legal; the group simply comes off the board.
        bool isSuicide = captured.Count == 0 && !rulesEngine.HasLiberties(nearest.x, nearest.y);

        // Update ko point for White's next normal Go move.
        Vector2Int? nextKo = null;
        if (!isSuicide && captured.Count == 1)
        {
            var placedGroup = rulesEngine.GetGroupStones(nearest.x, nearest.y);
            if (placedGroup.Count == 1)
            {
                int libs = rulesEngine.CountLiberties(nearest.x, nearest.y);
                if (libs == 1)
                {
                    nextKo = new Vector2Int(captured[0].ix, captured[0].iy);
                }
            }
        }
        goKoPoint = nextKo;

        if (isSuicide)
        {
            var selfGroup = rulesEngine.GetGroupStones(nearest.x, nearest.y);
            foreach (var s in selfGroup) RemoveStone(s);
        }

        // One-time initial stone is now done; White proceeds to normal Phase One
        blackInitialStonePending = false;
        phaseOne = true;
        currentPlayer = PieceColor.White;
    }

    List<Vector2Int> GenerateExtendedReachMoves(Piece piece)
    {
        // before generating new highlights, clear any previous origin highlight
        if (lastMoveFromSquare.HasValue)
        {
            // origin squares are part of normal board highlights; we simply
            // regenerate highlights after each move, so no extra clear needed here.
        }

        // Start with ordinary chess moves as always-legal baselines
        var standardMoves = GenerateStandardMoves(piece);
        var result = new HashSet<Vector2Int>(standardMoves);

        var frontier = new Queue<(Vector2Int from, bool fromIsTerritory)>();
        var visited = new HashSet<Vector2Int>();
        frontier.Enqueue((piece.square, /*fromIsTerritory*/ false));
        visited.Add(piece.square);

        bool firstLayer = true;

        while (frontier.Count > 0)
        {
            int layerCount = frontier.Count;

            for (int i = 0; i < layerCount; i++)
            {
                var node = frontier.Dequeue();
                var from = node.from;
                bool fromIsTerritory = node.fromIsTerritory;

                List<Vector2Int> stepMoves;
                if (firstLayer)
                {
                    // First hop: piece stands on its actual square and may pass through non-territory empties.
                    // Only requirement for this hop is that the landing square is own territory.
                    stepMoves = GenerateFirstHopChainSteps(piece);
                }
                else
                {
                    // Subsequent hops: must both start AND land in own territory,
                    // and (except for knights) move only through own territory.
                    if (!fromIsTerritory)
                        continue; // cannot hop further from a non-territory origin

                    var originalSquare = piece.square;
                    piece.square = from;
                    stepMoves = GenerateNonCaptureTerritoryMoves(piece);
                    piece.square = originalSquare;
                }

                foreach (var to in stepMoves)
                {
                    if (!visited.Add(to))
                        continue;

                    bool toIsTerritory = rulesEngine.IsSquareOwnTerritory(piece, to);

                    // You may only LAND on own territory in the chain.
                    if (!toIsTerritory)
                        continue;

                    frontier.Enqueue((to, toIsTerritory));

                    // Any own-territory step we can actually stand on is a legal destination
                    result.Add(to);
                }
            }

            firstLayer = false;
        }

        result.Remove(piece.square);
        return new List<Vector2Int>(result);
    }

    // First-hop chain steps: origin = piece.square, may pass through non-territory empties.
    // Only landing square must be in own territory (enforced in GenerateExtendedReachMoves).
    List<Vector2Int> GenerateFirstHopChainSteps(Piece piece)
    {
        var steps = new List<Vector2Int>();
        int x = piece.square.x, y = piece.square.y;
        var color = piece.color;
        bool InBounds(int a, int b) => a >= 0 && b >= 0 && a < boardManager.boardSize && b < boardManager.boardSize;
        bool IsEmpty(int a, int b) => boardManager.GetSquarePiece(new Vector2Int(a, b)) == null;

        void TryAddRay(int dx, int dy)
        {
            int nx = x + dx, ny = y + dy;
            while (InBounds(nx, ny) && IsEmpty(nx, ny))
            {
                // Ray can cross any empty squares, we don't care about territory here.
                steps.Add(new Vector2Int(nx, ny));
                nx += dx; ny += dy;
            }
        }

        void TryAddKnight(int nx, int ny)
        {
            if (!InBounds(nx, ny)) return;
            if (!IsEmpty(nx, ny)) return;
            steps.Add(new Vector2Int(nx, ny));
        }

        switch (piece.type)
        {
            case PieceType.Pawn:
                int dir = color == PieceColor.White ? 1 : -1;
                if (InBounds(x, y + dir) && IsEmpty(x, y + dir)) steps.Add(new Vector2Int(x, y + dir));
                break;
            case PieceType.Rook:
                TryAddRay(1, 0); TryAddRay(-1, 0); TryAddRay(0, 1); TryAddRay(0, -1);
                break;
            case PieceType.Bishop:
                TryAddRay(1, 1); TryAddRay(-1, 1); TryAddRay(1, -1); TryAddRay(-1, -1);
                break;
            case PieceType.Queen:
                TryAddRay(1, 0); TryAddRay(-1, 0); TryAddRay(0, 1); TryAddRay(0, -1);
                TryAddRay(1, 1); TryAddRay(-1, 1); TryAddRay(1, -1); TryAddRay(-1, -1);
                break;
            case PieceType.Knight:
                int[,] deltas = new int[,] { { 1, 2 }, { 2, 1 }, { -1, 2 }, { -2, 1 }, { 1, -2 }, { 2, -1 }, { -1, -2 }, { -2, -1 } };
                for (int i = 0; i < 8; i++)
                    TryAddKnight(x + deltas[i, 0], y + deltas[i, 1]);
                break;
            case PieceType.King:
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (InBounds(nx, ny) && IsEmpty(nx, ny)) steps.Add(new Vector2Int(nx, ny));
                    }
                break;
        }

        return steps;
    }

    // One-step non-capture moves staying entirely within own territory for that step.
    // - start square is assumed to already be own territory (caller enforces this)
    // - destination must also be own territory
    // - sliding pieces (rook/bishop/queen) may NOT pass through non-territory
    // - knights may jump over non-territory but must land on own territory
    List<Vector2Int> GenerateNonCaptureTerritoryMoves(Piece piece)
    {
        var steps = new List<Vector2Int>();
        int x = piece.square.x, y = piece.square.y;
        var color = piece.color;
        bool InBounds(int a, int b) => a >= 0 && b >= 0 && a < boardManager.boardSize && b < boardManager.boardSize;
        bool IsEmpty(int a, int b) => boardManager.GetSquarePiece(new Vector2Int(a, b)) == null;

        bool IsOwnTerritory(int a, int b)
        {
            if (!InBounds(a, b)) return false;
            return rulesEngine.IsSquareOwnTerritory(piece, new Vector2Int(a, b));
        }

        void TryAddSlide(int dx, int dy)
        {
            int nx = x + dx, ny = y + dy;
            while (InBounds(nx, ny) && IsEmpty(nx, ny) && IsOwnTerritory(nx, ny))
            {
                // ensure all intermediate squares (between origin and dest) are own territory too
                int cx = x + dx, cy = y + dy;
                bool allTerritory = true;
                while (cx != nx || cy != ny)
                {
                    if (!IsOwnTerritory(cx, cy)) { allTerritory = false; break; }
                    cx += dx; cy += dy;
                }
                if (!allTerritory) break;

                steps.Add(new Vector2Int(nx, ny));

                nx += dx; ny += dy;
            }
        }

        void TryAddKnight(int nx, int ny)
        {
            if (!InBounds(nx, ny)) return;
            if (!IsEmpty(nx, ny)) return;
            if (!IsOwnTerritory(nx, ny)) return; // knight may jump over, but must land in territory
            steps.Add(new Vector2Int(nx, ny));
        }

        switch (piece.type)
        {
            case PieceType.Pawn:
                int dir = color == PieceColor.White ? 1 : -1;
                int ny = y + dir;
                if (IsOwnTerritory(x, y) && IsOwnTerritory(x, ny) && InBounds(x, ny) && IsEmpty(x, ny))
                    steps.Add(new Vector2Int(x, ny));
                break;

            case PieceType.Rook:
                TryAddSlide(1, 0); TryAddSlide(-1, 0); TryAddSlide(0, 1); TryAddSlide(0, -1);
                break;

            case PieceType.Bishop:
                TryAddSlide(1, 1); TryAddSlide(-1, 1); TryAddSlide(1, -1); TryAddSlide(-1, -1);
                break;

            case PieceType.Queen:
                TryAddSlide(1, 1); TryAddSlide(-1, 1); TryAddSlide(1, -1); TryAddSlide(-1, -1);
                break;

            case PieceType.Knight:
                int[,] deltas = new int[,] { { 1, 2 }, { 2, 1 }, { -1, 2 }, { -2, 1 }, { 1, -2 }, { 2, -1 }, { -1, -2 }, { -2, -1 } };
                for (int i = 0; i < 8; i++)
                {
                    int nx = x + deltas[i, 0];
                    int ny2 = y + deltas[i, 1];
                    TryAddKnight(nx, ny2);
                }
                break;

            case PieceType.King:
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, nyKing = y + dy;
                        if (!InBounds(nx, nyKing)) continue;
                        if (!IsEmpty(nx, nyKing)) continue;
                        if (!IsOwnTerritory(nx, nyKing)) continue;
                        steps.Add(new Vector2Int(nx, nyKing));
                    }
                break;
        }

        return steps;
    }

    // Standard chess moves without chain logic
    List<Vector2Int> GenerateStandardMoves(Piece piece)
    {
        var moves = new List<Vector2Int>();
        int x = piece.square.x; int y = piece.square.y;
        var color = piece.color;
        bool InBounds(int a, int b) => a >= 0 && b >= 0 && a < boardManager.boardSize && b < boardManager.boardSize;
        bool IsEmpty(int a, int b) => boardManager.GetSquarePiece(new Vector2Int(a, b)) == null;
        bool IsEnemy(int a, int b)
        {
            var p = boardManager.GetSquarePiece(new Vector2Int(a, b));
            return p != null && p.color != color;
        }

        switch (piece.type)
        {
            case PieceType.Pawn:
                int dir = color == PieceColor.White ? 1 : -1;
                int startRow = color == PieceColor.White ? 1 : 6;
                if (InBounds(x, y + dir) && IsEmpty(x, y + dir)) moves.Add(new Vector2Int(x, y + dir));
                if (y == startRow && InBounds(x, y + 2 * dir) && IsEmpty(x, y + dir) && IsEmpty(x, y + 2 * dir)) moves.Add(new Vector2Int(x, y + 2 * dir));
                int cx1 = x + 1, cy1 = y + dir; int cx2 = x - 1, cy2 = y + dir;
                if (InBounds(cx1, cy1) && IsEnemy(cx1, cy1)) moves.Add(new Vector2Int(cx1, cy1));
                if (InBounds(cx2, cy2) && IsEnemy(cx2, cy2)) moves.Add(new Vector2Int(cx2, cy2));

                // EN PASSANT: capture a pawn that just double-stepped and is adjacent
                void TryEnPassant(int sideX)
                {
                    int ex = x + sideX;
                    int ey = y;        // adjacent file, same rank
                    int ty = y + dir;  // target square behind that pawn

                    if (!InBounds(ex, ey) || !InBounds(ex, ty)) return;
                    var p = boardManager.GetSquarePiece(new Vector2Int(ex, ey));
                    if (p == null || p.type != PieceType.Pawn || p.color == color) return;
                    if (!p.justDoubleStepped) return;

                    moves.Add(new Vector2Int(ex, ty));
                }

                TryEnPassant(1);
                TryEnPassant(-1);
                break;

            case PieceType.Rook:
                AddSlides(moves, piece, 1, 0); AddSlides(moves, piece, -1, 0); AddSlides(moves, piece, 0, 1); AddSlides(moves, piece, 0, -1);
                break;
            case PieceType.Bishop:
                AddSlides(moves, piece, 1, 1); AddSlides(moves, piece, -1, 1); AddSlides(moves, piece, 1, -1); AddSlides(moves, piece, -1, -1);
                break;
            case PieceType.Queen:
                AddSlides(moves, piece, 1, 0); AddSlides(moves, piece, -1, 0); AddSlides(moves, piece, 0, 1); AddSlides(moves, piece, 0, -1);
                AddSlides(moves, piece, 1, 1); AddSlides(moves, piece, -1, 1); AddSlides(moves, piece, 1, -1); AddSlides(moves, piece, -1, -1);
                break;
            case PieceType.Knight:
                int[,] deltas = new int[,] { { 1, 2 }, { 2, 1 }, { -1, 2 }, { -2, 1 }, { 1, -2 }, { 2, -1 }, { -1, -2 }, { -2, -1 } };
                for (int i = 0; i < 8; i++)
                {
                    int nx = x + deltas[i, 0]; int ny = y + deltas[i, 1];
                    if (!InBounds(nx, ny)) continue;
                    if (IsEmpty(nx, ny) || IsEnemy(nx, ny)) moves.Add(new Vector2Int(nx, ny));
                }
                break;
            case PieceType.King:
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue; int nx = x + dx, ny = y + dy;
                    if (!InBounds(nx, ny)) continue;
                    if (IsEmpty(nx, ny) || IsEnemy(nx, ny)) moves.Add(new Vector2Int(nx, ny));
                }

                // CASTLING (simplified: no check detection, just unmoved pieces and empty path)
                if (!piece.hasMoved)
                {
                    void TryCastle(int rookX, int stepX)
                    {
                        int ky = y;
                        int kx = x;

                        if (!InBounds(rookX, ky)) return;
                        var rook = boardManager.GetSquarePiece(new Vector2Int(rookX, ky));
                        if (rook == null || rook.type != PieceType.Rook || rook.color != color || rook.hasMoved) return;

                        int curX = kx + stepX;
                        while (curX != rookX)
                        {
                            if (!InBounds(curX, ky)) return;
                            if (!IsEmpty(curX, ky)) return;
                            curX += stepX;
                        }

                        int destKx = x + 2 * stepX;
                        if (!InBounds(destKx, ky)) return;
                        if (!IsEmpty(destKx, ky)) return;

                        moves.Add(new Vector2Int(destKx, ky));
                    }

                    // assume rooks at 0 and 7 like normal chess
                    TryCastle(0, -1);
                    TryCastle(7, 1);
                }
                break;
        }
        return moves;
    }

    void AddSlides(List<Vector2Int> moves, Piece piece, int dx, int dy)
    {
        int x = piece.square.x;
        int y = piece.square.y;
        var color = piece.color;

        bool InBounds(int a, int b) => a >= 0 && b >= 0 && a < boardManager.boardSize && b < boardManager.boardSize;
        bool IsEmpty(int a, int b) => boardManager.GetSquarePiece(new Vector2Int(a, b)) == null;
        bool IsEnemy(int a, int b)
        {
            var p = boardManager.GetSquarePiece(new Vector2Int(a, b));
            return p != null && p.color != color;
        }

        int nx = x + dx;
        int ny = y + dy;

        while (InBounds(nx, ny))
        {
            if (IsEmpty(nx, ny))
            {
                moves.Add(new Vector2Int(nx, ny));
            }
            else
            {
                if (IsEnemy(nx, ny))
                    moves.Add(new Vector2Int(nx, ny));
                break;
            }

            nx += dx;
            ny += dy;
        }
    }

    void ExecuteMove(Piece piece, Vector2Int to, PieceType? promotion = null)
    {
        Vector2Int from = piece.square;

        bool pawnMoved = piece.type == PieceType.Pawn;

        // --- build a stable, copy/paste-friendly move token before mutating state ---
        bool isCastle = piece.type == PieceType.King && Mathf.Abs(to.x - from.x) == 2 && to.y == from.y;
        bool isEnPassant = false;
        PieceType? capturedType = null;

        // Detect en passant capture (diagonal pawn move onto empty destination square)
        if (piece.type == PieceType.Pawn)
        {
            var targetOnTo = boardManager.GetSquarePiece(to);
            if (targetOnTo == null && to.x != from.x)
            {
                int dir = piece.color == PieceColor.White ? 1 : -1;
                Vector2Int enemySq = new Vector2Int(to.x, to.y - dir);
                var epPawn = boardManager.GetSquarePiece(enemySq);
                if (epPawn != null && epPawn.type == PieceType.Pawn && epPawn.color != piece.color && epPawn.justDoubleStepped)
                {
                    isEnPassant = true;
                    capturedType = PieceType.Pawn;
                }
            }
        }

        // Normal capture on destination
        var preTarget = boardManager.GetSquarePiece(to);
        if (preTarget != null)
        {
            capturedType = preTarget.type;
        }

        // EN PASSANT: pawn moves diagonally to an empty square capturing a pawn that just double-stepped
        if (piece.type == PieceType.Pawn)
        {
            var targetOnTo = boardManager.GetSquarePiece(to);
            if (targetOnTo == null && to.x != from.x)
            {
                int dir = piece.color == PieceColor.White ? 1 : -1;
                Vector2Int enemySq = new Vector2Int(to.x, to.y - dir);
                var epPawn = boardManager.GetSquarePiece(enemySq);
                if (epPawn != null && epPawn.type == PieceType.Pawn && epPawn.color != piece.color && epPawn.justDoubleStepped)
                {
                    boardManager.SetSquarePiece(enemySq, null);
                    Destroy(epPawn.gameObject);
                }
            }
        }

        // normal capture on destination
        var target = boardManager.GetSquarePiece(to);
        if (target != null)
        {
            Destroy(target.gameObject);
        }

        // CASTLING: king moves two squares horizontally, slide rook
        if (piece.type == PieceType.King && Mathf.Abs(to.x - from.x) == 2 && to.y == from.y)
        {
            int dir = to.x > from.x ? 1 : -1;
            int rookFromX = dir == 1 ? 7 : 0;
            int rookToX = from.x + dir; // rook ends next to king on inside

            Vector2Int rookFrom = new Vector2Int(rookFromX, from.y);
            Vector2Int rookTo = new Vector2Int(rookToX, from.y);

            var rook = boardManager.GetSquarePiece(rookFrom);
            if (rook != null && rook.type == PieceType.Rook && rook.color == piece.color)
            {
                boardManager.SetSquarePiece(rookFrom, null);
                rook.square = rookTo;
                boardManager.SetSquarePiece(rookTo, rook);
                rook.transform.position = boardManager.SquareToWorld(rookTo.x, rookTo.y);
                rook.hasMoved = true;
            }
        }

        // move the piece
        boardManager.SetSquarePiece(piece.square, null);
        piece.square = to;
        boardManager.SetSquarePiece(to, piece);
        piece.transform.position = boardManager.SquareToWorld(to.x, to.y);

        lastMovedSquare = to;
        lastMoveFromSquare = from;

        ApplyPieceStoneInteractions(piece, to);

        // NOTE: Do NOT call CheckAllPiecesForSurroundCapture() here.
        // Pieces can safely move into already-surrounded squares; they are only
        // removed when the fourth surrounding stone is placed on a corner.

        // mark piece movement flags
        piece.hasMoved = true;
        if (piece.type == PieceType.Pawn)
        {
            piece.justDoubleStepped = Mathf.Abs(to.y - from.y) == 2;
        }
        else
        {
            piece.justDoubleStepped = false;
        }

        // PROMOTION: auto-promote when a pawn reaches the last rank.
        PieceType? appliedPromotion = null;
        if (pawnMoved)
        {
            int promoteRank = piece.color == PieceColor.White ? (boardManager.boardSize - 1) : 0;
            if (to.y == promoteRank)
            {
                appliedPromotion = promotion ?? PieceType.Queen;
                piece.type = appliedPromotion.Value;
                boardManager.RefreshPieceVisual(piece);
                piece.justDoubleStepped = false;
            }
        }

        // highlight origin square and possibly start pawn extra stone placement
        if (lastMoveFromSquare.HasValue)
        {
            boardManager.ShowOriginHighlight(lastMoveFromSquare.Value);
        }
        if (pawnMoved && !waitingForTerritoryClick)
        {
            StartPawnExtraStonePlacement(piece);
        }

        // Prepare move history token now, but delay final logging until EndTurn so we can include
        // the Go stone delta (captures/removals/bonus stones) associated with this chess move.
        BeginPendingMoveDebugToken(from, to, capturedType, isEnPassant, isCastle, appliedPromotion);
    }

    void BeginPendingMoveDebugToken(Vector2Int from, Vector2Int to, PieceType? capturedType, bool isEnPassant, bool isCastle, PieceType? promotion)
    {
        string token = SquareToDebugCoord(from) + SquareToDebugCoord(to);

        var tags = new List<string>();
        if (capturedType.HasValue) tags.Add("x" + PieceTypeToShort(capturedType.Value));
        if (isEnPassant) tags.Add("ep");
        if (isCastle) tags.Add("castle");
        if (promotion.HasValue) tags.Add("=" + PieceTypeToShort(promotion.Value));

        if (tags.Count > 0)
        {
            token += "[" + string.Join(",", tags) + "]";
        }

        _pendingChessMoveToken = token;
        CaptureGoSnapshotAfterChessMove();
    }

    void CaptureGoSnapshotAfterChessMove()
    {
        if (boardManager == null) return;

        int isz = boardManager.intersectionSize;
        if (_goSnapshotAfterChess == null || _goSnapshotAfterChess.GetLength(0) != isz || _goSnapshotAfterChess.GetLength(1) != isz)
        {
            _goSnapshotAfterChess = new sbyte[isz, isz];
        }

        for (int ix = 0; ix < isz; ix++)
        {
            for (int iy = 0; iy < isz; iy++)
            {
                var inter = boardManager.intersections[ix, iy];
                var occ = (inter != null) ? inter.occupant : null;
                if (occ == null)
                {
                    _goSnapshotAfterChess[ix, iy] = -1;
                }
                else
                {
                    _goSnapshotAfterChess[ix, iy] = (sbyte)(occ.color == StoneColor.White ? 0 : 1);
                }
            }
        }
    }

    void CommitPendingMoveDebugTokenWithGoDelta()
    {
        if (string.IsNullOrEmpty(_pendingChessMoveToken)) return;
        if (boardManager == null || _goSnapshotAfterChess == null)
        {
            // Still record the chess token even if we couldn't capture Go delta.
            _chessMoveHistory.Add(_pendingChessMoveToken);
            if (logChessMoveChain)
            {
                string chain = string.Join(" ", _chessMoveHistory);
                Debug.Log($"[Moves] last={_pendingChessMoveToken} chain={chain}");
            }
            _pendingChessMoveToken = null;
            return;
        }

        string goDelta = BuildGoDeltaString();
        string finalToken = string.IsNullOrEmpty(goDelta)
            ? _pendingChessMoveToken
            : _pendingChessMoveToken + "{go:" + goDelta + "}";

        _chessMoveHistory.Add(finalToken);

        if (logChessMoveChain)
        {
            string chain = string.Join(" ", _chessMoveHistory);
            Debug.Log($"[Moves] last={finalToken} chain={chain}");
        }

        _pendingChessMoveToken = null;
    }

    string BuildGoDeltaString()
    {
        int isz = boardManager.intersectionSize;
        var parts = new List<string>();

        for (int ix = 0; ix < isz; ix++)
        {
            for (int iy = 0; iy < isz; iy++)
            {
                sbyte oldVal = _goSnapshotAfterChess[ix, iy];
                var inter = boardManager.intersections[ix, iy];
                var occ = (inter != null) ? inter.occupant : null;
                sbyte newVal = (occ == null) ? (sbyte)-1 : (sbyte)(occ.color == StoneColor.White ? 0 : 1);

                if (oldVal == newVal) continue;

                if (oldVal != -1)
                {
                    parts.Add("-" + (oldVal == 0 ? "W" : "B") + "(" + ix + "," + iy + ")");
                }
                if (newVal != -1)
                {
                    parts.Add("+" + (newVal == 0 ? "W" : "B") + "(" + ix + "," + iy + ")");
                }
            }
        }

        return string.Join("", parts);
    }

    static string SquareToDebugCoord(Vector2Int sq)
    {
        char file = (char)('a' + Mathf.Clamp(sq.x, 0, 7));
        int rank = Mathf.Clamp(sq.y, 0, 7) + 1;
        return $"{file}{rank}";
    }

    static string PieceTypeToShort(PieceType t)
    {
        switch (t)
        {
            case PieceType.King: return "K";
            case PieceType.Queen: return "Q";
            case PieceType.Rook: return "R";
            case PieceType.Bishop: return "B";
            case PieceType.Knight: return "N";
            case PieceType.Pawn: return "P";
            default: return "?";
        }
    }

    public void OnFlipBoardButton()
    {
        blackPerspective = !blackPerspective;
        boardManager.FlipBoardVisual();
    }

    // NEW: handle entering enemy territory and setting up forced stone removal
    void ApplyPieceStoneInteractions(Piece piece, Vector2Int destSquare)
    {
        var owner = rulesEngine.TerritoryOwnerOfSquare(destSquare.x, destSquare.y);
        if (!owner.HasValue) return;

        var enemyColor = piece.color == PieceColor.White ? StoneColor.Black : StoneColor.White;
        if (owner.Value != enemyColor) return;

        // Destination is enemy territory. Check if any enemy stone is on a corner.
        if (!HasEnemyCornerStone(destSquare, enemyColor)) return;

        // Set state so Phase Two click removes one enemy stone.
        waitingForTerritoryClick = true;
    }

    void HandleTerritoryRemovalClick(Vector2 worldPos)
    {
        var enemyStoneColor = currentPlayer == PieceColor.White ? StoneColor.Black : StoneColor.White;

        var nearest = FindNearestIntersection(worldPos);
        if (nearest == null) return;

        if (!IsCornerOfSquare(nearest.x, nearest.y, lastMovedSquare)) return;

        if (nearest.occupant == null || nearest.occupant.color != enemyStoneColor) return;

        RemoveStone(nearest.occupant);

        waitingForTerritoryClick = false;

        // After removing a stone, newly-surrounded pieces may now be captured.
        CheckAllPiecesForSurroundCapture();

        // If the last moved piece was a pawn, start its extra stone selection now
        var pawn = boardManager.GetSquarePiece(lastMovedSquare);
        if (pawn != null && pawn.type == PieceType.Pawn)
        {
            StartPawnExtraStonePlacement(pawn);
        }
    }

    // NEW: begin pawn extra stone placement by collecting empty corners and waiting for a click
    void StartPawnExtraStonePlacement(Piece pawn)
    {
        pendingPawnCornerOptions.Clear();
        waitingForPawnStoneChoice = false;

        int sx = pawn.square.x;
        int sy = pawn.square.y;

        var c1 = boardManager.GetIntersection(sx, sy);
        var c2 = boardManager.GetIntersection(sx + 1, sy);
        var c3 = boardManager.GetIntersection(sx, sy + 1);
        var c4 = boardManager.GetIntersection(sx + 1, sy + 1);

        void TryAdd(Intersection inter)
        {
            if (inter == null) return;
            if (inter.occupant != null) return;
            pendingPawnCornerOptions.Add(inter);
        }

        TryAdd(c1);
        TryAdd(c2);
        TryAdd(c3);
        TryAdd(c4);

        if (pendingPawnCornerOptions.Count > 0)
        {
            waitingForPawnStoneChoice = true;
            // Optionally you could add some visual indication here (eg. highlight intersections)
        }
    }

    // NEW: handle the click that chooses which corner gets the pawn's extra stone
    void HandlePawnExtraStoneChoiceClick(Vector2 worldPos)
    {
        var nearest = FindNearestIntersection(worldPos);
        if (nearest == null) return;

        // Ensure that the clicked intersection is one of the pending options
        Intersection chosen = null;
        foreach (var inter in pendingPawnCornerOptions)
        {
            if (inter != null && inter.x == nearest.x && inter.y == nearest.y)
            {
                chosen = inter;
                break;
            }
        }

        if (chosen == null) return;

        // Place the extra stone for the current player's pawn
        var color = currentPlayer == PieceColor.White ? StoneColor.White : StoneColor.Black;
        PlaceStoneAtIntersection(chosen, color);

        // Resolve captures caused by this bonus stone
        var captured = rulesEngine.ResolveCapturesAfterPlacement(chosen.x, chosen.y);
        foreach (var s in captured)
        {
            RemoveStone(s);
        }

        // Surround resolves before the stone can die, matching the main-stone rule.
        CheckAllPiecesForSurroundCapture();

        // If no enemy was captured and this new stone's group has no liberties, it dies immediately
        if (captured.Count == 0 && !rulesEngine.HasLiberties(chosen.x, chosen.y))
        {
            var selfGroup = rulesEngine.GetGroupStones(chosen.x, chosen.y);
            foreach (var s in selfGroup)
            {
                RemoveStone(s);
            }
        }

        waitingForPawnStoneChoice = false;
        pendingPawnCornerOptions.Clear();
    }

    // Helpers previously present at the bottom of this file
    bool IsCornerOfSquare(int ix, int iy, Vector2Int square)
    {
        int sx = square.x;
        int sy = square.y;
        return (ix == sx && iy == sy) ||
               (ix == sx + 1 && iy == sy) ||
               (ix == sx && iy == sy + 1) ||
               (ix == sx + 1 && iy == sy + 1);
    }

    bool HasEnemyCornerStone(Vector2Int square, StoneColor enemyColor)
    {
        int sx = square.x;
        int sy = square.y;
        var c1 = boardManager.GetIntersection(sx, sy);
        var c2 = boardManager.GetIntersection(sx + 1, sy);
        var c3 = boardManager.GetIntersection(sx, sy + 1);
        var c4 = boardManager.GetIntersection(sx + 1, sy + 1);
        return (c1 != null && c1.occupant != null && c1.occupant.color == enemyColor) ||
               (c2 != null && c2.occupant != null && c2.occupant.color == enemyColor) ||
               (c3 != null && c3.occupant != null && c3.occupant.color == enemyColor) ||
               (c4 != null && c4.occupant != null && c4.occupant.color == enemyColor);
    }

    void CheckAllPiecesForSurroundCapture()
    {
        for (int x = 0; x < boardManager.boardSize; x++)
        for (int y = 0; y < boardManager.boardSize; y++)
        {
            var p = boardManager.squares[x, y];
            if (p == null) continue;
            if (IsPieceSurroundedByEnemyStones(p))
            {
                if (p.type == PieceType.King)
                {
                    gameOver = true;
                    winner = (p.color == PieceColor.White) ? PieceColor.Black : PieceColor.White;
                }
                boardManager.SetSquarePiece(p.square, null);
                Destroy(p.gameObject);
            }
        }
    }

    bool IsPieceSurroundedByEnemyStones(Piece piece)
    {
        var enemyColor = piece.color == PieceColor.White ? StoneColor.Black : StoneColor.White;
        int sx = piece.square.x;
        int sy = piece.square.y;
        var c1 = boardManager.GetIntersection(sx, sy);
        var c2 = boardManager.GetIntersection(sx + 1, sy);
        var c3 = boardManager.GetIntersection(sx, sy + 1);
        var c4 = boardManager.GetIntersection(sx + 1, sy + 1);

        if (c1 == null || c2 == null || c3 == null || c4 == null) return false;
        if (c1.occupant == null || c2.occupant == null || c3.occupant == null || c4.occupant == null) return false;

        return c1.occupant.color == enemyColor &&
               c2.occupant.color == enemyColor &&
               c3.occupant.color == enemyColor &&
               c4.occupant.color == enemyColor;
    }

    void PlaceStoneAtIntersection(Intersection inter, StoneColor color)
    {
        var prefab = color == StoneColor.White ? whiteStonePrefab : blackStonePrefab;
        if (prefab == null) return;
        Vector2 pos = boardManager.IntersectionToWorld(inter.x, inter.y);
        var go = Instantiate(prefab, new Vector3(pos.x, pos.y, 0f), Quaternion.identity, boardManager.transform);
        var stone = go.GetComponent<Stone>();
        if (stone == null) stone = go.AddComponent<Stone>();
        stone.Init(color, inter.x, inter.y);
        inter.occupant = stone;
    }

    Vector2Int? WorldToSquare(Vector2 world)
    {
        float localX = (world.x - boardManager.origin.x) / boardManager.squareSize;
        float localY = (world.y - boardManager.origin.y) / boardManager.squareSize;
        int sx = Mathf.FloorToInt(localX);
        int sy = Mathf.FloorToInt(localY);
        if (sx < 0 || sy < 0 || sx >= boardManager.boardSize || sy >= boardManager.boardSize) return null;

        // If the board visuals are flipped, map the clicked visual square back
        // to the underlying logical coordinates used by RulesEngine and squares[,].
        if (boardManager.isFlipped)
        {
            sx = boardManager.boardSize - 1 - sx;
            sy = boardManager.boardSize - 1 - sy;
        }

        return new Vector2Int(sx, sy);
    }

    Intersection FindNearestIntersection(Vector2 worldPos)
    {
        float minDist = float.MaxValue;
        Intersection best = null;
        float threshold = boardManager.squareSize * 0.85f; // increased threshold for easier clicking
        for (int x = 0; x < boardManager.intersectionSize; x++)
        for (int y = 0; y < boardManager.intersectionSize; y++)
        {
            var inter = boardManager.intersections[x, y];

            // Use the same flipped-aware IntersectionToWorld as the visuals do
            var pos = boardManager.IntersectionToWorld(x, y);
            float d = Vector2.Distance(pos, worldPos);
            if (d < minDist && d < threshold)
            {
                minDist = d;
                best = inter;
            }
        }
        return best;
    }

    bool PlaceStoneSafe(int ix, int iy, StoneColor color)
    {
        if (boardManager == null)
        {
            Debug.LogError("BoardManager not assigned on GameController.");
            return false;
        }
        var prefab = color == StoneColor.White ? whiteStonePrefab : blackStonePrefab;
        if (prefab == null)
        {
            Debug.LogError($"{color} stone prefab not assigned on BoardManager/GameController.");
            return false;
        }
        var inter = boardManager.GetIntersection(ix, iy);
        if (inter == null)
        {
            Debug.LogError($"Intersection ({ix},{iy}) is null.");
            return false;
        }
        if (inter.occupant != null)
        {
            return false;
        }

        Vector3 pos = new Vector3(boardManager.IntersectionToWorld(ix, iy).x, boardManager.IntersectionToWorld(ix, iy).y, 0f);
        var go = Instantiate(prefab, pos, Quaternion.identity, boardManager.transform);
        var stone = go.GetComponent<Stone>();
        if (stone == null) stone = go.AddComponent<Stone>();
        stone.Init(color, ix, iy);
        inter.occupant = stone;
        return true;
    }

    void RemoveStone(Stone s)
    {
        if (s == null) return;
        var inter = boardManager.GetIntersection(s.ix, s.iy);
        if (inter != null && inter.occupant == s)
        {
            inter.occupant = null;
        }
        Destroy(s.gameObject);
    }

    void EndTurn()
    {
        // Finish logging the chess move token with the associated Go delta for this turn.
        CommitPendingMoveDebugTokenWithGoDelta();

        currentPlayer = currentPlayer == PieceColor.White ? PieceColor.Black : PieceColor.White;
        phaseOne = true;
        selectedPiece = null;
        legalMoves.Clear();
        boardManager.ClearHighlights();

        // Superko: remember the position we just reached so it cannot be recreated.
        RecordPositionForSuperko();

        // Clear justDoubleStepped on all pawns of the side to move now
        for (int x = 0; x < boardManager.boardSize; x++)
        for (int y = 0; y < boardManager.boardSize; y++)
        {
            var p = boardManager.squares[x, y];
            if (p != null && p.type == PieceType.Pawn && p.color == currentPlayer)
            {
                p.justDoubleStepped = false;
            }
        }
    }

    void RecordPositionForSuperko()
    {
        if (boardManager == null) return;

        var snapshot = SimStateBuilder.FromLiveGame(this, boardManager);
        if (snapshot == null) return;

        PositionHistory.Push(SimZobrist.ComputeBoardHash(snapshot));
    }

    // Would a main stone here recreate a position already seen? Runs once per placement
    // attempt, so the cost of snapshotting the board is irrelevant here.
    bool ViolatesSuperko(int ix, int iy)
    {
        if (boardManager == null) return false;

        var snapshot = SimStateBuilder.FromLiveGame(this, boardManager);
        if (snapshot == null) return false;

        var color = currentPlayer == PieceColor.White ? SimStoneColor.White : SimStoneColor.Black;

        // If this is the only placement available, superko has to yield rather than
        // leave the player with nowhere legal to play.
        var legal = SimRules.GenerateAllLegalFullTurns(snapshot, applySuperko: true);
        if (legal != null && legal.Count == 0) return false;

        return SimRules.IsSuperkoViolation(snapshot, ix, iy, color);
    }

    bool IsAIToMove()
    {
        if (currentPlayer == PieceColor.White)
            return whiteIsAI;
        else
            return blackIsAI;
    }

    void RunAiTurn()
    {
        if (gameOver) return;

        // Special-case: initial black stone happens before any White move.
        // If Black is AI, it places the stone; otherwise we wait for the human click.
        if (blackInitialStonePending)
        {
            if (blackIsAI)
            {
                RunAiBlackInitialStonePlacement();
            }
            return;
        }

        if (phaseOne)
        {
            RunAiPhaseOne_UsingSim();
        }
        else if (waitingForTerritoryClick)
        {
            // TODO: implement AI for territory removal using sim
            RunAiTerritoryRemoval();
        }
        else if (waitingForPawnStoneChoice)
        {
            // TODO: implement AI for pawn bonus stone using sim
            RunAiPawnBonusStone();
        }
        else
        {
            // Phase two Go stone placement (keep existing logic for now)
            RunAiPhaseTwoStonePlacement();
        }
    }

    void RunAiPhaseOne_UsingSim()
    {
        if (boardManager == null) return;

        // Apply evaluation knobs for this search.
        SimRules.useMobilityEval = aiUseMobilityEval;
        SimRules.mobilityWeight = aiMobilityWeight;

        // 1. Build sim snapshot
        SimState root = SimStateBuilder.FromLiveGame(this, boardManager);
        if (root == null) return;

        // 2. Ask search for best turn (currently chess-only)
        SimTurn best = SimSearch.FindBestTurn(root, aiMaxDepth, aiTimeBudgetMs);
        if (!best.chessMove.HasValue)
        {
            // No chess move found; end phase one and let phase two proceed.
            phaseOne = false;
            return;
        }

        SimChessMove move = best.chessMove.Value;

        if (aiLogMissedQueenCaptures)
        {
            LogIfMissedImmediateQueenCapture(root, best);
        }

        // 3. Apply to live game using existing ExecuteMove
        Piece piece = boardManager.GetSquarePiece(move.from);
        if (piece == null || piece.color != currentPlayer)
        {
            // Inconsistent state; bail out
            phaseOne = false;
            return;
        }

        ExecuteMove(piece, move.to, move.promotion);

        // Clear selection/highlights like a human move
        selectedPiece = null;
        legalMoves.Clear();
        boardManager.ClearHighlights();

        // End phase one after AI move
        phaseOne = false;
    }

    void LogIfMissedImmediateQueenCapture(SimState root, SimTurn chosen)
    {
        if (root == null || !chosen.chessMove.HasValue) return;

        var turns = SimRules.GenerateAllLegalFullTurns(root);
        if (turns == null || turns.Count == 0) return;

        var queenCaptures = new List<SimChessMove>();
        foreach (var t in turns)
        {
            if (!t.chessMove.HasValue) continue;
            var mv = t.chessMove.Value;

            var dest = root.squares[mv.to.x, mv.to.y];
            if (!dest.HasValue) continue;
            if (dest.Value.type != PieceType.Queen) continue;

            var mover = root.squares[mv.from.x, mv.from.y];
            if (!mover.HasValue) continue;
            if (dest.Value.color == mover.Value.color) continue;

            queenCaptures.Add(mv);
        }

        if (queenCaptures.Count == 0) return;

        // If the chosen move is itself a queen capture, nothing to log.
        var chosenMv = chosen.chessMove.Value;
        var chosenDest = root.squares[chosenMv.to.x, chosenMv.to.y];
        bool chosenIsQueenCapture = chosenDest.HasValue && chosenDest.Value.type == PieceType.Queen;
        if (chosenIsQueenCapture) return;

        // Emit a concise debug message listing candidate queen captures.
        string chosenTok = SquareToDebugCoord(chosenMv.from) + SquareToDebugCoord(chosenMv.to);
        var parts = new List<string>();
        foreach (var mv in queenCaptures)
        {
            parts.Add(SquareToDebugCoord(mv.from) + SquareToDebugCoord(mv.to));
        }

        Debug.Log($"[AI] Missed immediate queen capture(s): choices={string.Join(",", parts)} chosen={chosenTok}");
    }

    // Simple AI: choose a random legal initial stone placement for Black.
    void RunAiBlackInitialStonePlacement()
    {
        if (!blackInitialStonePending || boardManager == null || rulesEngine == null) return;

        var valid = new List<Intersection>();
        for (int ix = 0; ix < boardManager.intersectionSize; ix++)
        {
            for (int iy = 0; iy < boardManager.intersectionSize; iy++)
            {
                var inter = boardManager.intersections[ix, iy];
                if (inter == null || inter.occupant != null) continue;
                valid.Add(inter);
            }
        }

        if (valid.Count == 0) return;

        var choice = valid[Random.Range(0, valid.Count)];
        Vector2 worldPos = boardManager.IntersectionToWorld(choice.x, choice.y);
        HandleBlackInitialStonePlacement(worldPos);
    }

    // Placeholder AI for territory removal: pick the first removable enemy stone by reusing click logic.
    void RunAiTerritoryRemoval()
    {
        if (!waitingForTerritoryClick || boardManager == null) return;

        var enemyStoneColor = currentPlayer == PieceColor.White ? StoneColor.Black : StoneColor.White;
        var sq = lastMovedSquare;

        // Defensive: if we don't have a last moved square, we can't legally remove.
        if (sq.x < 0 || sq.y < 0)
        {
            waitingForTerritoryClick = false;
            return;
        }

        // Only corners of the last moved square are legal removal targets.
        var options = new List<Intersection>();
        var c1 = boardManager.GetIntersection(sq.x, sq.y);
        var c2 = boardManager.GetIntersection(sq.x + 1, sq.y);
        var c3 = boardManager.GetIntersection(sq.x, sq.y + 1);
        var c4 = boardManager.GetIntersection(sq.x + 1, sq.y + 1);

        void TryAdd(Intersection inter)
        {
            if (inter == null) return;
            if (inter.occupant == null) return;
            if (inter.occupant.color != enemyStoneColor) return;
            options.Add(inter);
        }

        TryAdd(c1);
        TryAdd(c2);
        TryAdd(c3);
        TryAdd(c4);

        if (options.Count == 0)
        {
            // Nothing removable (should be rare). Clear the flag to avoid stalling.
            waitingForTerritoryClick = false;
            return;
        }

        // Choose the removal that is best in the sim.
        Intersection bestChoice = options[0];
        int bestScore = int.MinValue;

        SimState root = SimStateBuilder.FromLiveGame(this, boardManager);
        if (root != null)
        {
            root.phaseOne = false;
            SimStoneColor expectedEnemy = enemyStoneColor == StoneColor.White ? SimStoneColor.White : SimStoneColor.Black;

            foreach (var opt in options)
            {
                SimState child = root.DeepCopy();
                SimRules.ApplyTerritoryRemoval(child, new Vector2Int(opt.x, opt.y), expectedEnemy);
                int score = SimRules.EvaluateForSideToMove(child);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestChoice = opt;
                }
            }
        }

        var choice = bestChoice;
        Vector2 worldPos = boardManager.IntersectionToWorld(choice.x, choice.y);
        HandleTerritoryRemovalClick(worldPos);
    }

    // Placeholder AI for pawn bonus stone choice.
    void RunAiPawnBonusStone()
    {
        if (!waitingForPawnStoneChoice || boardManager == null) return;

        // If no options are available, clear to avoid stalling.
        if (pendingPawnCornerOptions == null || pendingPawnCornerOptions.Count == 0)
        {
            waitingForPawnStoneChoice = false;
            pendingPawnCornerOptions?.Clear();
            return;
        }

        // Choose the bonus placement that is best in the sim.
        Intersection bestChoice = pendingPawnCornerOptions[0];
        int bestScore = int.MinValue;

        SimState root = SimStateBuilder.FromLiveGame(this, boardManager);
        if (root != null)
        {
            root.phaseOne = false;
            SimStoneColor c = currentPlayer == PieceColor.White ? SimStoneColor.White : SimStoneColor.Black;

            foreach (var opt in pendingPawnCornerOptions)
            {
                SimState child = root.DeepCopy();
                SimRules.ApplyGoBonusPawnStone(child, new SimStonePlacement
                {
                    intersection = new Vector2Int(opt.x, opt.y),
                    color = c
                });

                int score = SimRules.EvaluateForSideToMove(child);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestChoice = opt;
                }
            }
        }

        var choice = bestChoice;
        Vector2 worldPos = boardManager.IntersectionToWorld(choice.x, choice.y);
        HandlePawnExtraStoneChoiceClick(worldPos);
    }

    // Placeholder AI for phase-two stone placement: keep existing random/naive behavior.
    void RunAiPhaseTwoStonePlacement()
    {
        if (boardManager == null || rulesEngine == null) return;

        // Build sim snapshot and force phaseTwo.
        SimState root = SimStateBuilder.FromLiveGame(this, boardManager);
        if (root == null) return;
        root.phaseOne = false;

        // Apply evaluation knobs for this search.
        SimRules.useMobilityEval = aiUseMobilityEval;
        SimRules.mobilityWeight = aiMobilityWeight;

        // Phase 2 is very branchy (up to ~81 legal moves). Keep depth modest.
        int depth = Mathf.Clamp(aiMaxDepth, 1, 2);
        int budgetMs = Mathf.Max(1, aiTimeBudgetMs);

        SimTurn best = SimSearch.FindBestTurn(root, depth, budgetMs);
        if (!best.mainStone.HasValue)
        {
            // Fallback to random if sim couldn't find a legal stone.
            var candidates = new List<Intersection>();
            for (int ix = 0; ix < boardManager.intersectionSize; ix++)
            for (int iy = 0; iy < boardManager.intersectionSize; iy++)
            {
                var inter = boardManager.intersections[ix, iy];
                if (inter == null || inter.occupant != null) continue;
                candidates.Add(inter);
            }
            if (candidates.Count == 0) return;
            var choice = candidates[Random.Range(0, candidates.Count)];
            Vector2 wp = boardManager.IntersectionToWorld(choice.x, choice.y);
            HandleBoardClick_Legacy(wp);
            return;
        }

        var placement = best.mainStone.Value;
        Vector2 worldPos = boardManager.IntersectionToWorld(placement.intersection.x, placement.intersection.y);
        HandleBoardClick_Legacy(worldPos);
    }
}
