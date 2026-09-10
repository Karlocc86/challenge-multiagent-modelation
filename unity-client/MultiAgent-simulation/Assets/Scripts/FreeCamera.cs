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

    [Header("Seguimiento")]
    [Tooltip("Donde se planta la camara respecto al votante que sigue.")]
    public Vector3 offsetSeguimiento = new Vector3(0f, 3.5f, -5f);
    public float suavizadoSeguimiento = 8f;

    [Header("Sacudida")]
    [Tooltip("Magnitud viva del temblor. La escribe SimulationRunner; 0 = quieta.")]
    public float sacudidaActual = 0f;

    float yaw;
    float pitch;
    Transform objetivo;
    // Lo que le sumamos a la camara el frame pasado. Se resta al empezar el
    // siguiente para que la posicion base sea siempre la real: sin esto la
    // sacudida se acumularia, o habria que guardar una posicion "original" que
    // quedaria obsoleta en cuanto la camara se moviera.
    Vector3 sacudidaAplicada = Vector3.zero;

    public bool Siguiendo => objetivo != null;

    public void Seguir(Transform t) => objetivo = t;

    public void SoltarObjetivo()
    {
        objetivo = null;
        // Resincronizar con la rotacion real: si no, el primer clic derecho tras
        // soltar pega un tiron al angulo que habia antes de empezar a seguir.
        var e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180f ? e.x - 360f : e.x;
    }

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

        // El clic derecho siempre significa "yo tomo el control", asi que tambien
        // es el gesto para soltar a quien estemos siguiendo.
        if (mouse.rightButton.wasPressedThisFrame && Siguiendo) SoltarObjetivo();

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
        {
            if (Siguiendo)
            {
                // Siguiendo a alguien la rueda encuadra, no desplaza: mover la
                // camara la sacaria de su sitio y el seguimiento la regresaria.
                var acercado = offsetSeguimiento * (1f - Mathf.Sign(scroll) * 0.1f);
                if (acercado.magnitude > 2f && acercado.magnitude < 40f)
                    offsetSeguimiento = acercado;
            }
            else
            {
                transform.position += transform.forward * Mathf.Sign(scroll) * scrollSpeed * Time.deltaTime;
            }
        }

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

    // Unico sitio donde se resuelve la posicion final de la camara. Va en
    // LateUpdate para leer al votante despues de que SimulationRunner lo movio;
    // en Update arrastraria un frame de retraso, visible a x20.
    void LateUpdate()
    {
        // Quitar la sacudida del frame anterior antes de decidir nada.
        transform.position -= sacudidaAplicada;
        sacudidaAplicada = Vector3.zero;

        if (objetivo != null)
        {
            float k = 1f - Mathf.Exp(-suavizadoSeguimiento * Time.deltaTime);
            transform.position = Vector3.Lerp(
                transform.position, objetivo.position + offsetSeguimiento, k);
            var mira = objetivo.position + Vector3.up * 1.2f - transform.position;
            if (mira.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, Quaternion.LookRotation(mira, Vector3.up), k);
        }

        // Y volver a aplicarla sobre lo que haya quedado. Al terminar el temblor
        // la camara queda exactamente donde estaba, sin teletransporte.
        if (sacudidaActual > 0f)
        {
            sacudidaAplicada = (Vector3)(Random.insideUnitCircle * sacudidaActual);
            transform.position += sacudidaAplicada;
        }
    }
}
