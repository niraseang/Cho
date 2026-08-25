using UnityEngine;

public enum StoneColor { White, Black }

public class Stone : MonoBehaviour
{
    public StoneColor color;
    public int ix, iy; // intersection coords

    public void Init(StoneColor c, int x, int y)
    {
        color = c;
        ix = x;
        iy = y;
    }
}
