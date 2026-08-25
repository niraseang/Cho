using UnityEngine;
using UnityEngine.UI;

public class UIManager : MonoBehaviour
{
    public GameController controller;
    public Text currentPlayerText;

    void Awake()
    {
        controller = Object.FindFirstObjectByType<GameController>();
    }

    void Update()
    {
        if (currentPlayerText != null && controller != null)
            currentPlayerText.text = "Current: " + controller.currentPlayer.ToString() + (controller != null && controller != null ? (controller != null && !controller.Equals(null) ? "" : "") : "");
    }
}
