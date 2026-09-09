// FreeCamera.cs
// Camara libre estilo videojuego, usando el Input System nuevo (el que el
// proyecto ya trae configurado). No requiere cambiar Project Settings, asi
// que funciona para cualquiera que clone el repo.
//
// Ponlo en Assets/Scripts/ y agregalo como componente a la Main Camera.
//
// Controles:
//   - Mantener CLIC DERECHO y mover el mouse: mirar alrededor
//   - Con clic derecho: W/A/S/D para moverte, Q/E para bajar/subir
//   - Rueda del mouse: acercarte y alejarte
//   - Shift: moverte mas rapido

using UnityEngine;
using UnityEngine.InputSystem;

public class FreeCamera : MonoBehaviour
{
    [Header("Velocidades")]
    public float moveSpeed = 10f;
    public float fastMultiplier = 3f;
    public float lookSensitivity = 0.1f;
    public float scrollSpeed = 30f;

    float yaw;
    float pitch;

    void Start()
    {
        var e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x;
    }

    void Update()
    {
        var mouse = Mouse.current;
        var kb = Keyboard.current;
        if (mouse == null || kb == null) return;   // sin teclado/raton no hay nada que hacer

        // Rueda: acerca/aleja en la direccion de la vista (siempre activa).
        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
            transform.position += transform.forward * Mathf.Sign(scroll) * scrollSpeed * Time.deltaTime;

        // Lo demas solo con el clic derecho presionado.
        if (!mouse.rightButton.isPressed) return;

        Vector2 delta = mouse.delta.ReadValue();
        yaw   += delta.x * lookSensitivity;
        pitch -= delta.y * lookSensitivity;
        pitch  = Mathf.Clamp(pitch, -89f, 89f);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

        float speed = moveSpeed * (kb.leftShiftKey.isPressed ? fastMultiplier : 1f);
        Vector3 dir = Vector3.zero;
        if (kb.wKey.isPressed) dir += transform.forward;
        if (kb.sKey.isPressed) dir -= transform.forward;
        if (kb.dKey.isPressed) dir += transform.right;
        if (kb.aKey.isPressed) dir -= transform.right;
        if (kb.eKey.isPressed) dir += Vector3.up;
        if (kb.qKey.isPressed) dir -= Vector3.up;

        transform.position += dir * speed * Time.deltaTime;
    }
}
