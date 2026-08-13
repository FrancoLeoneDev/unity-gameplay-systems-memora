using UnityEngine;
using UnityEngine.UI;

public class PC_PictureSection : MonoBehaviour
{
    [SerializeField] private Button backButton;// Button to go back to the previous section
    [SerializeField] private Button printButton;// Button to print the picture


    private void Awake()
    {
        backButton.onClick.AddListener(Back); // Assign the Back method to the back button
    }

    private void Back()
    {
        gameObject.SetActive(false); // Deactivate the picture section
    }
}
