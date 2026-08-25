using System.Collections.Generic;
using UnityEngine;

public class BoardManager : MonoBehaviour
{
    public int boardSize = 8; // squares
    public int intersectionSize = 9; // intersections (boardSize + 1)

    public GameObject whiteStonePrefab;
    public GameObject blackStonePrefab;
    public GameObject piecePrefab;

    // NEW: optional flip button that will be wired as a clickable board-flip control
    [Header("UI / Controls")]
    public GameObject flipButtonObject;
    public Sprite flipButtonSprite; // sprite to use for the flip button visual

    // NEW: logical board orientation; false = white at bottom, true = flipped
    public bool isFlipped = false;

    // squares and intersections
    public Piece[,] squares;
    public Intersection[,] intersections;

    // cell size in world units
    public float squareSize = 1f;
    public Vector2 origin = Vector2.zero; // bottom-left corner of the board in world space

    public bool autoCenter = true;

    [Header("Piece Sprites (optional)")]
    public Sprite whitePawnSprite, whiteRookSprite, whiteKnightSprite, whiteBishopSprite, whiteQueenSprite, whiteKingSprite;
    public Sprite blackPawnSprite, blackRookSprite, blackKnightSprite, blackBishopSprite, blackQueenSprite, blackKingSprite;

    [Header("Highlighting")]
    public GameObject highlightPrefab; // a simple sprite (e.g., semi-transparent square) for move targets
    public GameObject originHighlightPrefab; // NEW: separate prefab for origin-square highlight
    private readonly List<GameObject> _activeHighlights = new List<GameObject>();
    GameObject _originHighlightInstance; // NEW: dedicated instance for origin highlight

    // NEW: checkered board colors and parent
    // Use strong brown contrast so it's clearly not grey
    public Color lightSquareColor = new Color(0.9f, 0.7f, 0.4f, 1f); // bright tan
    public Color darkSquareColor  = new Color(0.4f, 0.2f, 0.05f, 1f); // very dark brown
    GameObject squaresParent;

    void Awake()
    {
        if (autoCenter)
        {
            origin = new Vector2(-boardSize * squareSize * 0.5f, -boardSize * squareSize * 0.5f);
        }
        InitializeGrid();
        CreateCheckeredSquares();
        CreateOrConfigureFlipButton();
    }

    void Start()
    {
        // Ensure the button has a collider so central click handler can raycast it
        ConfigureFlipButtonCollider();
    }

    void CreateOrConfigureFlipButton()
    {
        // If a button was not provided in the inspector, create one as a child
        if (flipButtonObject == null)
        {
            flipButtonObject = new GameObject("FlipButton");
            flipButtonObject.transform.SetParent(transform, false);
        }

        // Ensure there is a SpriteRenderer and give it a sprite/color so it is visible
        var sr = flipButtonObject.GetComponent<SpriteRenderer>();
        if (sr == null)
        {
            sr = flipButtonObject.AddComponent<SpriteRenderer>();
        }
        sr.sprite = flipButtonSprite;   // may be null; assign in inspector
        sr.color  = (flipButtonSprite != null) ? Color.white : Color.yellow; // bright color if no sprite
        sr.sortingOrder = 100; // render on top

        // Position the button to the left of the board, vertically centered
        float boardHeight = boardSize * squareSize;
        float centerY = origin.y + boardHeight * 0.5f;
        float leftX = origin.x - squareSize * 1.0f; // one square to the left of the board
        flipButtonObject.transform.position = new Vector3(leftX, centerY, -1f); // slightly in front of pieces

        // Scale it to roughly squareSize
        flipButtonObject.transform.localScale = new Vector3(squareSize * 0.8f, squareSize * 0.8f, 1f);
    }

    // NEW: configure collider so a central input handler can raycast and detect the flip button
    void ConfigureFlipButtonCollider()
    {
        if (flipButtonObject == null) return;

        var col = flipButtonObject.GetComponent<Collider2D>();
        if (col == null)
        {
            col = flipButtonObject.AddComponent<BoxCollider2D>();
        }
        col.isTrigger = false;
    }

    public void InitializeGrid()
    {
        squares = new Piece[boardSize, boardSize];
        intersections = new Intersection[intersectionSize, intersectionSize];

        // clear old children first (in case of re-init)
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child.name.StartsWith("Intersection_") || child.name.StartsWith("GridLine_"))
            {
                DestroyImmediate(child.gameObject);
            }
        }

        // create intersection holders and position them in world space
        for (int x = 0; x < intersectionSize; x++)
        for (int y = 0; y < intersectionSize; y++)
        {
            GameObject go = new GameObject($"Intersection_{x}_{y}");
            go.transform.parent = this.transform;
            var inter = go.AddComponent<Intersection>();
            inter.x = x;
            inter.y = y;
            Vector2 worldPos = IntersectionToWorld(x, y);
            go.transform.position = new Vector3(worldPos.x, worldPos.y, 0f);
            intersections[x, y] = inter;
        }

        // create visible grid lines for the board so the user can see where to click
        CreateGridLines();
    }

    // convert square coords (0..boardSize-1) to world position (center of square)
    public Vector2 SquareToWorld(int x, int y)
    {
        if (isFlipped)
        {
            x = boardSize - 1 - x;
            y = boardSize - 1 - y;
        }

        float wx = origin.x + (x + 0.5f) * squareSize;
        float wy = origin.y + (y + 0.5f) * squareSize;
        return new Vector2(wx, wy);
    }

    // convert intersection coords (0..intersectionSize-1) to world position (intersection point)
    public Vector2 IntersectionToWorld(int ix, int iy)
    {
        if (isFlipped)
        {
            // Intersections run 0..intersectionSize-1, so mirror across that range
            ix = intersectionSize - 1 - ix;
            iy = intersectionSize - 1 - iy;
        }

        float wx = origin.x + ix * squareSize;
        float wy = origin.y + iy * squareSize;
        return new Vector2(wx, wy);
    }

    // helper: get intersection by coords safely
    public Intersection GetIntersection(int ix, int iy)
    {
        if (ix < 0 || iy < 0 || ix >= intersectionSize || iy >= intersectionSize) return null;
        return intersections[ix, iy];
    }

    // helper: get square occupant
    public Piece GetSquarePiece(Vector2Int sq)
    {
        if (sq.x < 0 || sq.y < 0 || sq.x >= boardSize || sq.y >= boardSize) return null;
        return squares[sq.x, sq.y];
    }

    public void SetSquarePiece(Vector2Int sq, Piece p)
    {
        if (sq.x < 0 || sq.y < 0 || sq.x >= boardSize || sq.y >= boardSize) return;
        squares[sq.x, sq.y] = p;
    }

    // draw simple grid lines using LineRenderers (no external sprites required)
    void CreateGridLines()
    {
        // remove any previously created grid children
        for (int i = this.transform.childCount - 1; i >= 0; i--)
        {
            var c = this.transform.GetChild(i);
            if (c.name.StartsWith("GridLine_")) DestroyImmediate(c.gameObject);
        }

        Material lineMat = new Material(Shader.Find("Sprites/Default"));
        Color lineColor = Color.black;
        float lineWidth = 0.03f;

        float boardWidth = boardSize * squareSize;
        float boardHeight = boardSize * squareSize;

        // vertical lines (from x=0..boardSize)
        for (int xi = 0; xi <= boardSize; xi++)
        {
            float x = origin.x + xi * squareSize;
            Vector3 start = new Vector3(x, origin.y, 0f);
            Vector3 end = new Vector3(x, origin.y + boardHeight, 0f);
            var go = new GameObject($"GridLine_V_{xi}");
            go.transform.parent = this.transform;
            var lr = go.AddComponent<LineRenderer>();
            lr.material = lineMat;
            lr.positionCount = 2;
            lr.SetPosition(0, start);
            lr.SetPosition(1, end);
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.startColor = lineColor;
            lr.endColor = lineColor;
            lr.sortingOrder = 0;
            lr.useWorldSpace = true;
        }

        // horizontal lines (from y=0..boardSize)
        for (int yi = 0; yi <= boardSize; yi++)
        {
            float y = origin.y + yi * squareSize;
            Vector3 start = new Vector3(origin.x, y, 0f);
            Vector3 end = new Vector3(origin.x + boardWidth, y, 0f);
            var go = new GameObject($"GridLine_H_{yi}");
            go.transform.parent = this.transform;
            var lr = go.AddComponent<LineRenderer>();
            lr.material = lineMat;
            lr.positionCount = 2;
            lr.SetPosition(0, start);
            lr.SetPosition(1, end);
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.startColor = lineColor;
            lr.endColor = lineColor;
            lr.sortingOrder = 0;
            lr.useWorldSpace = true;
        }
    }

    void CreateCheckeredSquares()
    {
        if (squaresParent != null)
        {
            DestroyImmediate(squaresParent);
        }
        squaresParent = new GameObject("Squares");
        squaresParent.transform.SetParent(transform, false);

        for (int x = 0; x < boardSize; x++)
        for (int y = 0; y < boardSize; y++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = $"Square_{x}_{y}";
            go.transform.SetParent(squaresParent.transform, false);

            Vector2 world = SquareToWorld(x, y);
            go.transform.position = new Vector3(world.x, world.y, 1f); // slightly behind pieces
            go.transform.localScale = new Vector3(squareSize, squareSize, 1f);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                // always make a fresh material so editor overrides don’t leak in
                var mat = new Material(Shader.Find("Unlit/Color"));
                bool isLight = ((x + y) % 2 == 0);
                Color c = isLight ? lightSquareColor : darkSquareColor;
                mat.color = c;
                mr.sharedMaterial = mat; // use sharedMaterial so Scene view shows the color directly
            }
        }
    }

    public void SetupInitialPieces()
    {
        // Clear any existing piece references
        for (int x = 0; x < boardSize; x++)
        for (int y = 0; y < boardSize; y++)
        {
            squares[x, y] = null;
        }

        // White pieces (bottom, y=0,1)
        SpawnPiece(PieceColor.White, PieceType.Rook,   new Vector2Int(0, 0));
        SpawnPiece(PieceColor.White, PieceType.Knight, new Vector2Int(1, 0));
        SpawnPiece(PieceColor.White, PieceType.Bishop, new Vector2Int(2, 0));
        SpawnPiece(PieceColor.White, PieceType.Queen,  new Vector2Int(3, 0));
        SpawnPiece(PieceColor.White, PieceType.King,   new Vector2Int(4, 0));
        SpawnPiece(PieceColor.White, PieceType.Bishop, new Vector2Int(5, 0));
        SpawnPiece(PieceColor.White, PieceType.Knight, new Vector2Int(6, 0));
        SpawnPiece(PieceColor.White, PieceType.Rook,   new Vector2Int(7, 0));
        for (int x = 0; x < 8; x++) SpawnPiece(PieceColor.White, PieceType.Pawn, new Vector2Int(x, 1));

        // Black pieces (top, y=7,6)
        SpawnPiece(PieceColor.Black, PieceType.Rook,   new Vector2Int(0, 7));
        SpawnPiece(PieceColor.Black, PieceType.Knight, new Vector2Int(1, 7));
        SpawnPiece(PieceColor.Black, PieceType.Bishop, new Vector2Int(2, 7));
        SpawnPiece(PieceColor.Black, PieceType.Queen,  new Vector2Int(3, 7));
        SpawnPiece(PieceColor.Black, PieceType.King,   new Vector2Int(4, 7));
        SpawnPiece(PieceColor.Black, PieceType.Bishop, new Vector2Int(5, 7));
        SpawnPiece(PieceColor.Black, PieceType.Knight, new Vector2Int(6, 7));
        SpawnPiece(PieceColor.Black, PieceType.Rook,   new Vector2Int(7, 7));
        for (int x = 0; x < 8; x++) SpawnPiece(PieceColor.Black, PieceType.Pawn, new Vector2Int(x, 6));
    }

    public Piece SpawnPiece(PieceColor color, PieceType type, Vector2Int squareCoord)
    {
        if (piecePrefab == null)
        {
            Debug.LogError("BoardManager.piecePrefab not assigned.");
            return null;
        }
        Vector3 pos = new Vector3(SquareToWorld(squareCoord.x, squareCoord.y).x, SquareToWorld(squareCoord.x, squareCoord.y).y, 0f);
        var go = Instantiate(piecePrefab, pos, Quaternion.identity, this.transform);
        var p = go.GetComponent<Piece>();
        if (p == null) p = go.AddComponent<Piece>();
        p.Init(color, type, squareCoord);
        // optional sprite setup
        var sr = go.GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            sr.sortingOrder = 10;
            sr.sprite = GetPieceSprite(color, type);

            // --- auto-scale so piece fits inside one square ---
            if (sr.sprite != null)
            {
                var bounds = sr.sprite.bounds; // in local units of the sprite
                float spriteHeight = bounds.size.y;
                if (spriteHeight > 0f)
                {
                    float targetHeight = squareSize * 0.9f;
                    float scale = targetHeight / spriteHeight;
                    go.transform.localScale = new Vector3(scale, scale, 1f);
                }
            }
        }
        squares[squareCoord.x, squareCoord.y] = p;
        return p;
    }

    Sprite GetPieceSprite(PieceColor c, PieceType t)
    {
        switch (c)
        {
            case PieceColor.White:
                switch (t)
                {
                    case PieceType.Pawn: return whitePawnSprite;
                    case PieceType.Rook: return whiteRookSprite;
                    case PieceType.Knight: return whiteKnightSprite;
                    case PieceType.Bishop: return whiteBishopSprite;
                    case PieceType.Queen: return whiteQueenSprite;
                    case PieceType.King: return whiteKingSprite;
                }
                break;
            case PieceColor.Black:
                switch (t)
                {
                    case PieceType.Pawn: return blackPawnSprite;
                    case PieceType.Rook: return blackRookSprite;
                    case PieceType.Knight: return blackKnightSprite;
                    case PieceType.Bishop: return blackBishopSprite;
                    case PieceType.Queen: return blackQueenSprite;
                    case PieceType.King: return blackKingSprite;
                }
                break;
        }
        return null;
    }

    public void RefreshPieceVisual(Piece piece)
    {
        if (piece == null) return;

        var sr = piece.GetComponent<SpriteRenderer>();
        if (sr == null) return;

        sr.sortingOrder = 10;
        sr.sprite = GetPieceSprite(piece.color, piece.type);

        // Keep the same auto-scale behavior as SpawnPiece so promoted pieces look correct.
        if (sr.sprite != null)
        {
            var bounds = sr.sprite.bounds;
            float spriteHeight = bounds.size.y;
            if (spriteHeight > 0f)
            {
                float targetHeight = squareSize * 0.9f;
                float scale = targetHeight / spriteHeight;
                piece.transform.localScale = new Vector3(scale, scale, 1f);
            }
        }
    }

    public void ClearHighlights()
    {
        foreach (var h in _activeHighlights)
        {
            if (h != null) Destroy(h);
        }
        _activeHighlights.Clear();
    }

    public void HighlightSquares(IEnumerable<Vector2Int> squaresToHighlight)
    {
        ClearHighlights();
        if (highlightPrefab == null) return;

        foreach (var sq in squaresToHighlight)
        {
            if (sq.x < 0 || sq.y < 0 || sq.x >= boardSize || sq.y >= boardSize) continue;
            Vector2 pos = SquareToWorld(sq.x, sq.y);
            var go = Instantiate(highlightPrefab, new Vector3(pos.x, pos.y, 0f), Quaternion.identity, transform);
            _activeHighlights.Add(go);
        }
    }

    public void ShowOriginHighlight(Vector2Int square)
    {
        if (_originHighlightInstance != null)
        {
            Destroy(_originHighlightInstance);
            _originHighlightInstance = null;
        }

        var prefab = originHighlightPrefab != null ? originHighlightPrefab : highlightPrefab;
        if (prefab == null) return;

        if (square.x < 0 || square.y < 0 || square.x >= boardSize || square.y >= boardSize) return;
        Vector2 pos = SquareToWorld(square.x, square.y);
        _originHighlightInstance = Instantiate(prefab, new Vector3(pos.x, pos.y, 0f), Quaternion.identity, transform);
    }

    public void ClearOriginHighlight()
    {
        if (_originHighlightInstance != null)
        {
            Destroy(_originHighlightInstance);
            _originHighlightInstance = null;
        }
    }

    public void FlipBoardVisual()
    {
        // Toggle logical orientation
        isFlipped = !isFlipped;

        // Reposition all existing pieces according to the new orientation
        for (int x = 0; x < boardSize; x++)
        for (int y = 0; y < boardSize; y++)
        {
            Piece p = squares[x, y];
            if (p == null) continue;

            Vector2 world = SquareToWorld(x, y);
            var t = p.transform;
            t.position = new Vector3(world.x, world.y, t.position.z);
        }

        // ALSO reposition all existing stones according to the new orientation
        for (int ix = 0; ix < intersectionSize; ix++)
        for (int iy = 0; iy < intersectionSize; iy++)
        {
            var inter = intersections[ix, iy];
            if (inter == null || inter.occupant == null) continue;

            Vector2 w = IntersectionToWorld(ix, iy);
            var st = inter.occupant.transform;
            st.position = new Vector3(w.x, w.y, st.position.z);
        }

        // Rebuild grid lines and checkered squares to match new orientation
        CreateGridLines();
        CreateCheckeredSquares();

        // Clear highlights (they will be recalculated by GameController when needed)
        ClearHighlights();
        ClearOriginHighlight();
    }
}
