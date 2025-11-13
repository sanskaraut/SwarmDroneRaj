using UnityEngine;

public class FreeFlyCamera : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 10f;
    public float fastSpeedMultiplier = 3f;
    public float upDownSpeed = 5f;

    [Header("Mouse Look Settings")]
    public float lookSensitivity = 2f;
    public float maxLookAngle = 89f;

    private float yaw = 0f;
    private float pitch = 0f;

    void Start()
    {
        Vector3 rot = transform.localRotation.eulerAngles;
        yaw = rot.y;
        pitch = rot.x;

        // Lock cursor while in play mode
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void Update()
    {
        HandleMovement();
        HandleMouseLook();
    }

    void HandleMovement()
    {
        float speed = moveSpeed;

        // Hold Shift to move faster
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            speed *= fastSpeedMultiplier;

        Vector3 direction = Vector3.zero;

        // WASD for forward/back/left/right
        if (Input.GetKey(KeyCode.W)) direction -= transform.forward;
        if (Input.GetKey(KeyCode.S)) direction += transform.forward;
        if (Input.GetKey(KeyCode.A)) direction += transform.right;
        if (Input.GetKey(KeyCode.D)) direction -= transform.right;

        // Q/E for vertical movement
        if (Input.GetKey(KeyCode.E)) direction += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) direction -= Vector3.up;

        if (direction.magnitude > 0.1f)
        {
            transform.position += direction.normalized * speed * Time.deltaTime;
        }
    }

    void HandleMouseLook()
    {
        // Rotate only when right mouse button is held
        if (Input.GetMouseButton(1))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            float mouseX = Input.GetAxis("Mouse X") * lookSensitivity;
            float mouseY = Input.GetAxis("Mouse Y") * lookSensitivity;

            yaw += mouseX;
            pitch -= mouseY;

            pitch = Mathf.Clamp(pitch, -maxLookAngle, maxLookAngle);

            transform.localRotation = Quaternion.Euler(pitch, yaw, 0f);
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
