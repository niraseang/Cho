using UnityEngine;

public class Intersection : MonoBehaviour
{
    public int x;
    public int y;
    public Stone occupant; // null if empty

    public bool IsEmpty => occupant == null;
}
