// SimulationRunner.cs
// Ponlo en Assets/Scripts/ dentro del proyecto de Unity.
//
// Requiere el paquete Newtonsoft.Json: Window > Package Manager > + > Add package
// by name > com.unity.nuget.newtonsoft-json

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using Newtonsoft.Json;

public class SimulationRunner : MonoBehaviour
{
    [Header("Servidor Flask")]
    public string serverUrl = "http://127.0.0.1:5000/simulate";
    public int numVoters = 50;
    // double, no float: Newtonsoft manda ~7 digitos para un float, y Python leeria
    // 0.3333333 en vez de 1/3, cambiando la corrida con la misma semilla.
    [Tooltip("PROMEDIO de llegadas por minuto simulado. Los huecos entre llegada y " +
             "llegada siguen siendo aleatorios alrededor de este promedio. Debe ser " +
             "mayor que 0. 0.333 = una llegada cada 3 minutos en promedio.")]
    public double promedioLlegadasPorMinuto = 1.0 / 3.0;
    public int seed = 7;
    public int secretarioCapacity = 6;
    public int mesaCapacity = 3;
    public int casillaCapacity = 8;
    public int urnaCapacity = 2;

    [Header("Prefabs y reproduccion")]
    public GameObject prefabHombre;
    public GameObject prefabMujer;
    [Tooltip("Velocidad de reproduccion: 60 = 1 min simulado por segundo real.")]
    public float speed = 60f;
    [Tooltip("Grados extra de giro en Y si los modelos miran al lado equivocado. Prueba 90, -90 o 180.")]
    public float yawOffset = 90f;

    // Un enum publico se dibuja como desplegable en el inspector, asi que no hay
    // forma de escribir un tipo que el backend no conozca. Va antes del [Header]
    // porque ese atributo solo es valido sobre campos, no sobre una declaracion
    // de tipo.
    public enum EventoExternoForzado { Aleatorio, CorteDeLuz, Temblor, Aguacero }

    [Header("Eventos externos")]
    [Tooltip("Aleatorio = lo decide la semilla, como siempre. Cualquier otro valor " +
             "fuerza ese evento sin cambiar el resto de la corrida: misma semilla, " +
             "misma gente, distinto clima.")]
    public EventoExternoForzado eventoForzado = EventoExternoForzado.Aleatorio;
    [Tooltip("Minuto simulado en el que ocurre. 0 = lo decide la semilla. Si es " +
             "mayor que la jornada, el evento cae con la casilla ya vacia.")]
    public double minutoDelEvento = 0.0;
    [Tooltip("Cuanto dura, en minutos simulados. 0 = lo decide la semilla (entre 3 " +
             "y 10). Con speed=60, 20 aqui son 20 segundos reales.")]
    public double duracionDelEvento = 0.0;

    [Tooltip("Particle System de lluvia. Colocalo sobre el patio/entrada, con 'Play On Awake' desactivado.")]
    public ParticleSystem lluviaVFX;
    [Tooltip("Luz(es) que se apagan durante un corte_de_luz.")]
    public Light lucesCasilla;
    [Tooltip("Que tanto se sacude la camara durante un temblor.")]
    public float shakeMagnitud = 0.15f;

    [Header("Controles de reproduccion")]
    [Tooltip("Multiplicadores que ofrecen los botones de velocidad. Se aplican " +
             "sobre el Speed de arriba, asi que x1 siempre es el valor que pusiste.")]
    public float[] multiplicadores = { 0.5f, 1f, 5f, 20f };

    [Header("Panel en pantalla")]
    [Tooltip("Hora simulada a la que arranca la jornada (24h). 8 = 8:00 AM.")]
    public int horaInicio = 8;
    [Tooltip("Mostrar el panel con reloj y contadores.")]
    public bool mostrarPanel = true;

    // Contadores en vivo (se recalculan cada frame desde los eventos ya ocurridos).
    int llegados, atendidos, rechazados;
    bool jornadaTerminada;

    // -------------------------------------------------------------------
    // Contrato JSON del backend
    // -------------------------------------------------------------------
    [System.Serializable]
    class Timeline
    {
        public Summary summary;
        public List<Movement> movements;
        public List<QueueEvent> queue_events;
        public List<StationEvent> station_events;
        public List<VoterEvent> voter_events;
        public List<ExternalEvent> external_events;
    }
    [System.Serializable]
    class Summary
    {
        public float duration_minutes;
        public int voters_arrived;
        public int voters_exited;
        public int voters_rejected;
        public Results results;
    }
    // Conteo de la urna. Solo cuenta a quien llego a la urna y salio: un
    // votante rechazado en el secretario nunca deposita su voto.
    [System.Serializable]
    class Results
    {
        public Dictionary<string, int> votes_by_candidate;
        public int total_votes;
        public string winner;              // null si hubo empate o si nadie voto
        public bool is_tie;
        public List<string> tied_candidates;
    }
    [System.Serializable] class Movement { public int voter; public string from; public string to; public float t_start; public float t_end; }
    // Un votante formado. `position` es su lugar en la fila de esa estacion, y
    // `queue_type` distingue la fila de adultos mayores de la general.
    [System.Serializable]
    class QueueEvent
    {
        public int voter;
        public string station;
        public string queue_type;
        public int position;
        public float t_join;
        public float t_leave;
    }
    [System.Serializable] class StationEvent { public int voter; public string station; public string @event; public float t; }
    [System.Serializable] class VoterEvent { public int voter; public string @event; public float t;
                                         public int edad; public string voto; public bool es_adulto_mayor; }
    [System.Serializable] class ExternalEvent { public string kind; public float t_start; public float duration; }

    // -------------------------------------------------------------------
    // Estado en runtime
    // -------------------------------------------------------------------
    Timeline timeline;
    float simClock;                                         // minutos simulados
    readonly Dictionary<string, Transform> anchors = new(); // nombre del empty -> transform
    readonly Dictionary<int, GameObject> voters = new();    // id -> GameObject
    readonly Dictionary<int, Movement> currentMove = new(); // id -> segmento activo
    readonly Dictionary<int, string> currentStation = new();// id -> etapa actual (si esta parado)
    readonly Dictionary<string, int[]> capacities = new();
    readonly Dictionary<string, int> nextSlot = new();      // reparto round-robin de slots
    readonly Dictionary<int, int> voterSlot = new();        // id -> slot asignado
    readonly Dictionary<int, QueueEvent> currentQueue = new(); // id -> fila en la que espera
    // Zig-zag de la entrada: QUEUE_GENERAL_000..045, en orden. Las distancias
    // acumuladas se calculan una vez porque las anclas no se mueven.
    readonly List<Vector3> rutaEntrada = new();
    readonly List<float> distanciaEnRuta = new();
    int movIdx, staIdx, votIdx, colaIdx, extIdx;
    ExternalEvent eventoActivo;
    float finEventoActivo;

    // -------------------------------------------------------------------
    // Controles de reproduccion. Todo mueve el mismo simClock que alimenta el
    // reloj del panel: pausar lo congela, la velocidad lo escala, y los saltos
    // lo escriben hacia adelante. Nunca hacia atras: los votantes destruidos en
    // EXIT no reaparecen y los pools de slots y filas son acumulados.
    // -------------------------------------------------------------------
    enum Accion { Ninguna, TogglePausa, SiguienteVotante, SiguienteEvento, CambiarVelocidad }
    Accion accionPendiente = Accion.Ninguna;
    int multiplicadorPedido = -1;
    bool pausado;
    float speedBase;
    int multiplicadorActivo;
    FreeCamera camara;

    int seleccionado = -1;
    int pendienteSeleccionar = -1;          // esperando a que su ARRIVAL lo instancie
    string desenlaceSeleccionado;           // "EXIT" | "REJECTED" | null si sigue dentro
    readonly List<float> tiemposDelSeleccionado = new();
    readonly List<string> etiquetasDelSeleccionado = new();
    readonly Dictionary<int, float> inicioEstacion = new();   // id -> t del SERVICE_START
    readonly Dictionary<int, VoterEvent> fichaPorVotante = new();

    void Start()
    {
        capacities["secretario"] = new int[secretarioCapacity];
        capacities["mesa"]       = new int[mesaCapacity];
        capacities["casilla"]    = new int[casillaCapacity];
        capacities["urna"]       = new int[urnaCapacity];
        foreach (var etapa in capacities.Keys) nextSlot[etapa] = 0;

        speedBase = speed;
        multiplicadorActivo = System.Array.IndexOf(multiplicadores, 1f);
        if (multiplicadorActivo < 0) multiplicadorActivo = 0;
        camara = Camera.main != null ? Camera.main.GetComponent<FreeCamera>() : null;
        if (camara == null)
            Debug.LogWarning("La Main Camera no tiene FreeCamera: no habra seguimiento ni sacudida.");

        IndexAnchors();
        StartCoroutine(FetchAndRun());
    }

    void IndexAnchors()
    {
        // Recolecta cualquier empty cuyo nombre empiece con nuestros prefijos.
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            var n = t.name.ToUpperInvariant();
            if (n.StartsWith("SPAWN") || n.StartsWith("EXIT") ||
                n.StartsWith("SLOT_") || n.StartsWith("QUEUE_") ||
                n.StartsWith("PATH_"))
            {
                anchors[t.name.ToUpperInvariant()] = t;
            }
        }
        if (anchors.Count == 0)
            Debug.LogWarning("No encontre anclas. Asegurate de que el FBX este como hijo de este GameObject.");

        ConstruirRutaEntrada();
    }

    // El zig-zag esta modelado como QUEUE_GENERAL_000, _001, ... en el orden en
    // que se recorre. Lo guardamos como una polilinea con sus distancias
    // acumuladas para poder interpolar a lo largo de ella a velocidad pareja.
    void ConstruirRutaEntrada()
    {
        rutaEntrada.Clear();
        distanciaEnRuta.Clear();
        for (int i = 0; ; i++)
        {
            var t = Anchor($"QUEUE_GENERAL_{i:D3}");
            if (!t) break;
            rutaEntrada.Add(t.position);
        }
        if (rutaEntrada.Count == 0) return;

        float acumulado = 0f;
        distanciaEnRuta.Add(0f);
        for (int i = 1; i < rutaEntrada.Count; i++)
        {
            acumulado += Vector3.Distance(rutaEntrada[i - 1], rutaEntrada[i]);
            distanciaEnRuta.Add(acumulado);
        }
    }

    // Camina de `desde` al zig-zag, lo recorre entero, y sale hacia `hasta`.
    // `u` avanza parejo en distancia, no por tramo, para que no acelere en las
    // vueltas cerradas.
    void PuntoEnRutaEntrada(Vector3 desde, Vector3 hasta, float u,
                            out Vector3 pos, out Vector3 dir)
    {
        float largoRuta = distanciaEnRuta[distanciaEnRuta.Count - 1];
        float entrada = Vector3.Distance(desde, rutaEntrada[0]);
        float salida = Vector3.Distance(rutaEntrada[rutaEntrada.Count - 1], hasta);
        float total = entrada + largoRuta + salida;
        float recorrido = Mathf.Clamp01(u) * total;

        if (recorrido <= entrada)
        {
            float k = entrada > 0f ? recorrido / entrada : 1f;
            pos = Vector3.Lerp(desde, rutaEntrada[0], k);
            dir = rutaEntrada[0] - desde;
            return;
        }
        if (recorrido >= entrada + largoRuta)
        {
            float k = salida > 0f ? (recorrido - entrada - largoRuta) / salida : 1f;
            var ultimo = rutaEntrada[rutaEntrada.Count - 1];
            pos = Vector3.Lerp(ultimo, hasta, k);
            dir = hasta - ultimo;
            return;
        }

        float dentro = recorrido - entrada;
        int i = 1;
        while (i < distanciaEnRuta.Count - 1 && distanciaEnRuta[i] < dentro) i++;
        float d0 = distanciaEnRuta[i - 1], d1 = distanciaEnRuta[i];
        float f = d1 > d0 ? (dentro - d0) / (d1 - d0) : 0f;
        pos = Vector3.Lerp(rutaEntrada[i - 1], rutaEntrada[i], f);
        dir = rutaEntrada[i] - rutaEntrada[i - 1];
    }

    IEnumerator FetchAndRun()
    {
        var body = JsonConvert.SerializeObject(new
        {
            num_voters = numVoters,
            arrival_rate = promedioLlegadasPorMinuto,
            seed,
            secretario_capacity = secretarioCapacity,
            mesa_capacity       = mesaCapacity,
            casilla_capacity    = casillaCapacity,
            urna_capacity       = urnaCapacity,
            // null en las tres = no forzar nada; el backend trata un null
            // explicito igual que una clave ausente.
            forced_event_kind     = KindDeEvento(eventoForzado),
            forced_event_time     = minutoDelEvento   > 0 ? (double?)minutoDelEvento   : null,
            forced_event_duration = duracionDelEvento > 0 ? (double?)duracionDelEvento : null,
        });

        using var req = new UnityWebRequest(serverUrl, "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();

        // ProtocolError = si hubo respuesta HTTP, pero con codigo de error. Sin
        // separarlo, un 400 por un parametro invalido se reportaba como si el
        // servidor estuviera apagado.
        if (req.result == UnityWebRequest.Result.ProtocolError)
        {
            Debug.LogError($"El backend rechazo la peticion (HTTP {req.responseCode}): " +
                           MensajeDeError(req.downloadHandler.text));
            yield break;
        }

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"El backend no responde: {req.error}. ¿Corriendo 'python server.py' en {serverUrl}?");
            yield break;
        }

        timeline = JsonConvert.DeserializeObject<Timeline>(req.downloadHandler.text);
        foreach (var e in timeline.voter_events)
            if (e.@event == "ARRIVAL") fichaPorVotante[e.voter] = e;

        Debug.Log($"Timeline recibido: {timeline.movements.Count} movimientos, " +
                  $"{timeline.station_events.Count} eventos de estacion, " +
                  $"{timeline.voter_events.Count} eventos de votante.");
    }

    // El inspector usa nombres legibles; el contrato del backend usa los suyos.
    static string KindDeEvento(EventoExternoForzado e) => e switch
    {
        EventoExternoForzado.CorteDeLuz => "corte_de_luz",
        EventoExternoForzado.Temblor    => "temblor",
        EventoExternoForzado.Aguacero   => "aguacero",
        _                               => null,   // Aleatorio: no se fuerza
    };

    // El backend reporta sus errores como {"error": "..."}. Un 500 inesperado
    // llega como HTML (la pagina de debug de Flask): ahi mostramos el texto crudo.
    static string MensajeDeError(string cuerpo)
    {
        if (string.IsNullOrWhiteSpace(cuerpo)) return "(respuesta vacia)";
        try
        {
            var payload = JsonConvert.DeserializeObject<Dictionary<string, string>>(cuerpo);
            if (payload != null && payload.TryGetValue("error", out var msg) && !string.IsNullOrEmpty(msg))
                return msg;
        }
        catch (JsonException) { /* no era JSON con forma de error */ }
        return cuerpo.Length > 300 ? cuerpo.Substring(0, 300) + "..." : cuerpo;
    }

    void Update()
    {
        if (timeline == null) return;

        // Los botones no mutan nada: encolan. OnGUI corre varias veces por frame
        // y cambiar el numero de controles entre la pasada de Layout y la de
        // Repaint lanza "Mismatched LayoutGroup".
        AplicarAccionPendiente();
        LeerTeclas();

        // El reloj se detiene cuando termina la jornada (sale el ultimo votante).
        float fin = timeline.summary != null ? timeline.summary.duration_minutes : float.MaxValue;
        if (simClock >= fin)
        {
            simClock = fin;
            jornadaTerminada = true;
        }
        else if (!pausado)
        {
            simClock += Time.deltaTime * (speed / 60f);   // speed = minutos simulados por segundo real
        }

        ProcessVoterEvents();
        ProcessQueueEvents();
        ProcessStationEvents();
        ProcessExternalEvents();
        ProcessMovements();
        InterpolateActiveMoves();
        UpdateAnimators();
        ResolverSeleccionPendiente();
    }

    // -------------------------------------------------------------------
    // Controles
    // -------------------------------------------------------------------
    void LeerTeclas()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.spaceKey.wasPressedThisFrame) accionPendiente = Accion.TogglePausa;
        if (kb.nKey.wasPressedThisFrame) accionPendiente = Accion.SiguienteVotante;
        if (kb.mKey.wasPressedThisFrame) accionPendiente = Accion.SiguienteEvento;
    }

    void AplicarAccionPendiente()
    {
        var a = accionPendiente;
        accionPendiente = Accion.Ninguna;
        switch (a)
        {
            case Accion.TogglePausa:      pausado = !pausado; break;
            case Accion.SiguienteVotante: SeleccionarSiguienteVotante(); break;
            case Accion.SiguienteEvento:  SaltarAlProximoEventoDelSeleccionado(); break;
            case Accion.CambiarVelocidad:
                multiplicadorActivo = multiplicadorPedido;
                speed = speedBase * multiplicadores[multiplicadorActivo];
                break;
        }
    }

    // Unico sitio donde el reloj se mueve a mano. Un salto grande es seguro: los
    // cinco while de consumo son inclusivos y drenan todo lo pendiente en el
    // mismo frame, incluidas las entradas y salidas de fila y de escritorio.
    void SaltarReloj(float destino)
    {
        float fin = timeline.summary != null ? timeline.summary.duration_minutes : float.MaxValue;
        destino = Mathf.Min(destino, fin);
        if (destino <= simClock) return;
        simClock = destino;
    }

    // -------------------------------------------------------------------
    // Seleccion y seguimiento
    // -------------------------------------------------------------------
    void SeleccionarSiguienteVotante()
    {
        var ids = voters.Where(kv => kv.Value != null).Select(kv => kv.Key).OrderBy(id => id).ToList();
        if (ids.Count == 0) { SaltarAlProximoArrival(); return; }

        // Los ids del backend empiezan en 1, asi que 0 sirve de centinela.
        int siguiente = ids.FirstOrDefault(id => id > seleccionado);
        if (siguiente == 0) siguiente = ids[0];
        Seleccionar(siguiente);
    }

    void SaltarAlProximoArrival()
    {
        if (jornadaTerminada) return;
        for (int i = votIdx; i < timeline.voter_events.Count; i++)
        {
            var e = timeline.voter_events[i];
            if (e.@event != "ARRIVAL" || e.t <= simClock) continue;
            SaltarReloj(e.t);
            pendienteSeleccionar = e.voter;   // su GameObject todavia no existe
            return;
        }
    }

    void ResolverSeleccionPendiente()
    {
        if (pendienteSeleccionar < 0) return;
        if (voters.TryGetValue(pendienteSeleccionar, out var go) && go != null)
        {
            Seleccionar(pendienteSeleccionar);
            return;
        }
        // Si su llegada ya quedo atras y aun asi no esta, soltarlo en vez de
        // quedarnos esperando para siempre.
        bool aunPorLlegar = timeline.voter_events.Any(
            e => e.voter == pendienteSeleccionar && e.@event == "ARRIVAL" && e.t > simClock);
        if (!aunPorLlegar) pendienteSeleccionar = -1;
    }

    void Seleccionar(int id)
    {
        seleccionado = id;
        pendienteSeleccionar = -1;
        desenlaceSeleccionado = null;
        ConstruirEventosDelSeleccionado(id);
        if (camara != null && voters.TryGetValue(id, out var go) && go != null)
            camara.Seguir(go.transform);
    }

    // Se arma una vez por seleccion: filtrar cuatro listas de miles de entradas
    // en cada frame seria absurdo, y no cambian despues del fetch.
    void ConstruirEventosDelSeleccionado(int id)
    {
        tiemposDelSeleccionado.Clear();
        etiquetasDelSeleccionado.Clear();
        // La prioridad decide que etiqueta gana cuando varios hitos caen en el
        // mismo instante: "Lo atiende secretario" dice mas que "Llega a
        // secretario", y los dos ocurren en el mismo minuto.
        var lista = new List<(float t, int prioridad, string etiqueta)>();

        foreach (var m in timeline.movements.Where(m => m.voter == id))
        {
            lista.Add((m.t_start, 0, $"Sale hacia {m.to}"));
            lista.Add((m.t_end, 0, $"Llega a {m.to}"));
        }
        if (timeline.queue_events != null)
            foreach (var q in timeline.queue_events.Where(q => q.voter == id))
            {
                lista.Add((q.t_join, 1, $"Se forma en {q.station}"));
                lista.Add((q.t_leave, 1, $"Le toca turno en {q.station}"));
            }
        foreach (var st in timeline.station_events.Where(st => st.voter == id))
            lista.Add((st.t, 2, st.@event == "SERVICE_START"
                                ? $"Lo atiende {st.station}" : $"Termina en {st.station}"));
        foreach (var v in timeline.voter_events.Where(v => v.voter == id))
            lista.Add((v.t, 3, v.@event == "ARRIVAL" ? "Llega a la casilla"
                             : v.@event == "REJECTED" ? "INE rechazada"
                             : "Sale de la casilla"));

        // Un solo paso por instante: saltar dos veces al mismo minuto no
        // avanzaria el reloj y el boton pareceria roto. Se agrupa por milesima
        // de minuto en vez de por EPS_EVENTO porque t*10000 pierde precision en
        // float con una jornada larga.
        foreach (var grupo in lista.GroupBy(x => Mathf.Round(x.t * 1000f))
                                   .OrderBy(g => g.Min(x => x.t)))
        {
            var mejor = grupo.OrderByDescending(x => x.prioridad).First();
            tiemposDelSeleccionado.Add(mejor.t);
            etiquetasDelSeleccionado.Add(mejor.etiqueta);
        }
    }

    // Margen para no volver a "saltar" al evento en el que ya estamos parados.
    const float EPS_EVENTO = 1e-4f;

    int IndiceDelProximoEvento()
    {
        for (int i = 0; i < tiemposDelSeleccionado.Count; i++)
            if (tiemposDelSeleccionado[i] > simClock + EPS_EVENTO) return i;
        return -1;
    }

    void SaltarAlProximoEventoDelSeleccionado()
    {
        int i = IndiceDelProximoEvento();
        if (i < 0) return;                  // sin mas eventos; el boton ya esta gris
        SaltarReloj(tiemposDelSeleccionado[i]);
    }

    string EstadoDelSeleccionado(out float desde)
    {
        desde = -1f;
        if (desenlaceSeleccionado == "EXIT") return "Salio de la casilla";
        if (desenlaceSeleccionado == "REJECTED") return "Rechazado: INE invalida";
        // currentMove primero: mientras camina hacia su lugar sigue registrado en
        // currentQueue, y decir "en fila" viendolo caminar seria mentir.
        if (currentMove.TryGetValue(seleccionado, out var m))
        { desde = m.t_start; return $"Caminando a {m.to}"; }
        if (currentStation.TryGetValue(seleccionado, out var est))
        { inicioEstacion.TryGetValue(seleccionado, out desde); return $"Lo atiende {est}"; }
        if (currentQueue.TryGetValue(seleccionado, out var q))
        {
            desde = q.t_join;
            return $"En fila de {q.station}" + (q.queue_type == "priority" ? " (prioritaria)" : "");
        }
        if (!voters.ContainsKey(seleccionado)) return "Aun no llega";
        return "Esperando";
    }

    // Convierte los minutos simulados en una hora de reloj "HH:MM AM/PM".
    string RelojSimulado()
    {
        int totalMin = horaInicio * 60 + Mathf.FloorToInt(simClock);
        int h = (totalMin / 60) % 24;
        int m = totalMin % 60;
        string ampm = h < 12 ? "AM" : "PM";
        int h12 = h % 12; if (h12 == 0) h12 = 12;
        return $"{h12:00}:{m:00} {ampm}";
    }

    GUIStyle _estilo, _estiloFin, _estiloGanador, _caja, _boton, _botonActivo, _ficha;
    Font _fuente;

    // Arial solo existe en Windows/Mac; en Linux el equivalente metrico es
    // Liberation Sans. Pedimos la primera que este REALMENTE instalada en vez
    // de asumir, porque pedir una ausente deja el panel sin dibujar texto.
    static Font CargarFuente(int size)
    {
        var instaladas = new HashSet<string>(Font.GetOSInstalledFontNames());
        foreach (var nombre in new[] { "Arial", "Liberation Sans", "DejaVu Sans",
                                       "Noto Sans", "Cantarell" })
        {
            if (instaladas.Contains(nombre))
                return Font.CreateDynamicFontFromOSFont(nombre, size);
        }
        // Ninguna de las conocidas: usamos lo que haya antes de quedarnos sin texto.
        var todas = Font.GetOSInstalledFontNames();
        return todas != null && todas.Length > 0
            ? Font.CreateDynamicFontFromOSFont(todas[0], size)
            : null;
    }

    void OnGUI()
    {
        if (!mostrarPanel || timeline == null) return;

        if (_estilo == null)
        {
            // El OnGUI de Unity 6 en URP no encuentra fuente por defecto; le
            // asignamos una explicitamente para que el texto se dibuje.
            _fuente = CargarFuente(20);
            _caja = new GUIStyle(GUI.skin.box);
            _estilo = new GUIStyle
            {
                font = _fuente, fontSize = 20, richText = true,
                normal = { textColor = Color.white },
                padding = new RectOffset(2, 2, 2, 2)
            };
            _estiloFin = new GUIStyle(_estilo)
            {
                fontSize = 22,
                normal = { textColor = new Color(1f, 0.4f, 0.3f) }
            };
            _estiloGanador = new GUIStyle(_estilo)
            {
                fontSize = 21,
                normal = { textColor = new Color(0.45f, 1f, 0.55f) }
            };
            // GUI.skin.button conserva su fondo; lo unico que le falta es la
            // fuente, por el mismo motivo que el resto del panel.
            _boton = new GUIStyle(GUI.skin.button) { font = _fuente, fontSize = 16 };
            _botonActivo = new GUIStyle(_boton)
            {
                normal = { textColor = new Color(0.45f, 1f, 0.55f) },
                hover  = { textColor = new Color(0.45f, 1f, 0.55f) }
            };
            _ficha = new GUIStyle(_estilo) { fontSize = 17 };
        }

        var res = timeline.summary != null ? timeline.summary.results : null;

        // El alto se calcula antes de dibujar y solo con estado que no cambia
        // dentro del OnGUI, porque los botones encolan en vez de mutar.
        int alto = 180 + 86;                       // dos filas de controles
        if (seleccionado >= 0) alto += 24 * 6;     // las seis lineas de la ficha
        else alto += 24;
        if (jornadaTerminada)
        {
            alto += 40;
            if (res != null && res.votes_by_candidate != null)
                alto += 34 + res.votes_by_candidate.Count * 24 + 34;
        }

        // 340 y no 300: cuatro botones de velocidad no caben en 280 de area.
        GUI.Box(new Rect(20, 20, 340, alto), GUIContent.none, _caja);
        GUILayout.BeginArea(new Rect(34, 30, 320, alto - 10));
        GUILayout.Label("<b>Casilla Especial - Andares</b>", _estilo);
        GUILayout.Space(6);
        GUILayout.Label($"Hora:  <b>{RelojSimulado()}</b>{(pausado ? "   PAUSA" : "")}", _estilo);
        GUILayout.Space(10);
        GUILayout.Label($"Llegaron:   <b>{llegados}</b>", _estilo);
        GUILayout.Label($"Votaron:    <b>{atendidos}</b>", _estilo);
        GUILayout.Label($"Rechazados: <b>{rechazados}</b>", _estilo);

        DibujarControles();
        DibujarFicha();

        if (jornadaTerminada)
        {
            GUILayout.Space(8);
            GUILayout.Label("JORNADA TERMINADA", _estiloFin);

            // El backend viejo no manda "results"; el panel simplemente lo omite.
            if (res != null && res.votes_by_candidate != null)
            {
                GUILayout.Space(8);
                GUILayout.Label($"<b>Votos ({res.total_votes})</b>", _estilo);

                foreach (var kv in res.votes_by_candidate.OrderByDescending(kv => kv.Value))
                {
                    float pct = res.total_votes > 0 ? 100f * kv.Value / res.total_votes : 0f;
                    GUILayout.Label($"  {kv.Key}:  <b>{kv.Value}</b>  ({pct:0.0}%)", _estilo);
                }

                GUILayout.Space(6);
                if (res.total_votes == 0)
                    GUILayout.Label("Sin votos emitidos", _estiloFin);
                else if (res.is_tie)
                    GUILayout.Label($"EMPATE: {string.Join(" - ", res.tied_candidates)}", _estiloFin);
                else
                    GUILayout.Label($"GANADOR: {res.winner}", _estiloGanador);
            }
        }
        GUILayout.EndArea();
    }

    void DibujarControles()
    {
        GUILayout.Space(10);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(pausado ? "Seguir" : "Pausa", _boton,
                             GUILayout.Width(88), GUILayout.Height(28)))
            accionPendiente = Accion.TogglePausa;
        if (GUILayout.Button("Sig. votante", _boton, GUILayout.Width(128), GUILayout.Height(28)))
            accionPendiente = Accion.SiguienteVotante;
        GUI.enabled = seleccionado >= 0 && IndiceDelProximoEvento() >= 0;
        if (GUILayout.Button("Sig. evento", _boton, GUILayout.Width(100), GUILayout.Height(28)))
            accionPendiente = Accion.SiguienteEvento;
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Velocidad:", _ficha, GUILayout.Width(96));
        for (int i = 0; i < multiplicadores.Length; i++)
        {
            var estilo = i == multiplicadorActivo ? _botonActivo : _boton;
            if (GUILayout.Button($"x{multiplicadores[i]:0.##}", estilo,
                                 GUILayout.Width(52), GUILayout.Height(26)))
            {
                accionPendiente = Accion.CambiarVelocidad;
                multiplicadorPedido = i;
            }
        }
        GUILayout.EndHorizontal();
    }

    void DibujarFicha()
    {
        GUILayout.Space(8);
        if (seleccionado < 0)
        {
            GUILayout.Label("Ningun votante seleccionado", _ficha);
            return;
        }

        // Un backend viejo no manda edad ni voto; ahi la ficha muestra "?".
        var f = fichaPorVotante.TryGetValue(seleccionado, out var fi) ? fi : null;
        bool conDatos = f != null && f.edad > 0;
        string edad = conDatos ? f.edad.ToString() : "?";
        string voto = f != null && !string.IsNullOrEmpty(f.voto) ? f.voto : "?";
        string mayor = conDatos ? (f.es_adulto_mayor ? "si" : "no") : "?";

        GUILayout.Label($"<b>Votante {seleccionado}</b>   Edad: {edad}", _ficha);
        GUILayout.Label($"Adulto mayor: {mayor}", _ficha);
        GUILayout.Label($"Voto: {voto}", _ficha);
        GUILayout.Label($"Estado: {EstadoDelSeleccionado(out float desde)}", _ficha);
        GUILayout.Label(desde >= 0f ? $"Lleva: {simClock - desde:0.0} min" : " ", _ficha);
        int i = IndiceDelProximoEvento();
        GUILayout.Label(i >= 0 ? $"Sigue: {etiquetasDelSeleccionado[i]}" : "Sin mas eventos", _ficha);
    }

    // Enciende "Caminando" en quien tiene un movimiento activo, lo apaga en los demas.
    void UpdateAnimators()
    {
        foreach (var (id, go) in voters)
        {
            if (go == null) continue;
            var anim = go.GetComponentInChildren<Animator>();
            if (anim == null) continue;
            anim.SetBool("Caminando", currentMove.ContainsKey(id));
        }
    }

    void ProcessVoterEvents()
    {
        var ev = timeline.voter_events;
        while (votIdx < ev.Count && ev[votIdx].t <= simClock)
        {
            var e = ev[votIdx++];
            if (e.@event == "ARRIVAL")
            {
                llegados++;
                var prefab = (Random.value < 0.5f) ? prefabHombre : prefabMujer;
                var spawn = Anchor("SPAWN");
                var go = Instantiate(prefab, spawn ? spawn.position : Vector3.zero, Quaternion.identity, transform);
                go.name = $"Votante_{e.voter}";
                voters[e.voter] = go;
            }
            else if (e.@event == "EXIT" || e.@event == "REJECTED")
            {
                if (e.@event == "EXIT") atendidos++;
                else rechazados++;
                if (e.voter == seleccionado)
                {
                    desenlaceSeleccionado = e.@event;
                    // La camara se queda donde esta: devolverla de golpe seria peor.
                    if (camara != null) camara.SoltarObjetivo();
                }
                inicioEstacion.Remove(e.voter);
                if (voters.TryGetValue(e.voter, out var go)) Destroy(go);
                voters.Remove(e.voter);
                currentMove.Remove(e.voter);
                currentStation.Remove(e.voter);
                SoltarLugarEnFila(e.voter);
                ReleaseSlot(e.voter);
            }
        }
    }

    // Coloca a cada votante formado en su lugar de la fila. Sin esto todos los
    // que esperan se quedan encimados en el escritorio de la estacion, porque su
    // ultimo movimiento los dejo ahi y nada los vuelve a mover hasta que les toca.
    void ProcessQueueEvents()
    {
        var ev = timeline.queue_events;
        if (ev == null) return;                     // backend viejo sin queue_events

        while (colaIdx < ev.Count && ev[colaIdx].t_join <= simClock)
        {
            var e = ev[colaIdx++];
            currentQueue[e.voter] = e;
            TomarLugarEnFila(e);
        }

        var yaPasaron = new List<int>();
        foreach (var (id, e) in currentQueue)
        {
            if (simClock >= e.t_leave) { yaPasaron.Add(id); continue; }
            if (!voters.TryGetValue(id, out var go) || go == null) { yaPasaron.Add(id); continue; }
            // Mientras camina hacia la fila lo mueve la interpolacion; solo lo
            // paramos en su lugar cuando ya llego.
            if (currentMove.ContainsKey(id)) continue;

            if (!anclaDeEspera.TryGetValue(id, out var anchor) || !anchor)
                continue;                           // sin lugar libre, se queda donde este
            // Solo la posicion del ancla: su rotacion puede venir tumbada del FBX,
            // igual que en los SLOT_. Los paramos verticales mirando al mueble.
            go.transform.position = anchor.position;
            go.transform.rotation = Quaternion.Euler(0f, 90f + yawOffset, 0f);
        }
        foreach (var id in yaPasaron) SoltarLugarEnFila(id);
    }

    // -------------------------------------------------------------------
    // Reparto de lugares en la fila.
    //
    // El FBX trae las filas repartidas por escritorio, QUEUE_<ESTACION>_<slot>_<pos>,
    // con QUEUE_LUGARES_POR_SLOT lugares en cada una.
    //
    // No usamos el `position` que manda el backend: ese numero es la posicion al
    // momento de formarse y no se actualiza cuando la fila avanza, asi que dos
    // personas distintas acaban registradas en el mismo lugar y se encimarian.
    // En vez de eso repartimos el primer lugar libre y lo soltamos al salir, igual
    // que hacemos con los escritorios. Como la fila es FIFO, el orden visual sale
    // casi igual al real. Los adultos mayores se reparten desde el otro extremo
    // para que su fila se distinga de la general.
    // -------------------------------------------------------------------
    const int QUEUE_LUGARES_POR_SLOT = 4;

    readonly Dictionary<string, int[]> lugaresDeFila = new();     // estacion -> ocupante por lugar
    // El ancla se resuelve una vez al asignar el lugar, no cada frame: en una
    // casilla saturada hay cientos de personas formadas a la vez.
    readonly Dictionary<int, Transform> anclaDeEspera = new();    // id -> ancla asignada

    int[] PoolDeFila(string estacion)
    {
        if (lugaresDeFila.TryGetValue(estacion, out var pool)) return pool;
        int slots = capacities.TryGetValue(estacion, out var servicio) ? servicio.Length : 1;
        pool = new int[Mathf.Max(1, slots) * QUEUE_LUGARES_POR_SLOT];
        lugaresDeFila[estacion] = pool;
        return pool;
    }

    void TomarLugarEnFila(QueueEvent e)
    {
        var pool = PoolDeFila(e.station.ToLowerInvariant());
        bool prioridad = e.queue_type == "priority";
        for (int i = 0; i < pool.Length; i++)
        {
            int idx = prioridad ? pool.Length - 1 - i : i;
            if (pool[idx] != 0) continue;
            var anchor = Anchor($"QUEUE_{e.station.ToUpperInvariant()}_" +
                                $"{idx / QUEUE_LUGARES_POR_SLOT:D2}_{idx % QUEUE_LUGARES_POR_SLOT:D2}");
            if (!anchor) continue;                  // lugar sin ancla modelada, probamos el siguiente
            pool[idx] = e.voter;
            anclaDeEspera[e.voter] = anchor;
            return;
        }
        // Fila mas larga que los lugares modelados: se queda sin lugar y no se
        // reposiciona, en vez de encimarse sobre alguien.
    }

    void SoltarLugarEnFila(int voterId)
    {
        currentQueue.Remove(voterId);
        if (!anclaDeEspera.Remove(voterId)) return;
        foreach (var pool in lugaresDeFila.Values)
            for (int i = 0; i < pool.Length; i++) if (pool[i] == voterId) pool[i] = 0;
    }

    void ProcessStationEvents()
    {
        var ev = timeline.station_events;
        while (staIdx < ev.Count && ev[staIdx].t <= simClock)
        {
            var e = ev[staIdx++];
            if (!voters.TryGetValue(e.voter, out var go)) continue;

            if (e.@event == "SERVICE_START")
            {
                SoltarLugarEnFila(e.voter);
                inicioEstacion[e.voter] = e.t;
                int slot = AssignSlot(e.voter, e.station);
                var anchor = Anchor($"SLOT_{e.station.ToUpperInvariant()}_{slot:D2}");
                // Solo la posicion del ancla; la rotacion del empty puede venir
                // tumbada del FBX. Paramos a la persona vertical y la giramos
                // hacia +X (donde esta el mueble) mas el offset del modelo.
                if (anchor)
                {
                    go.transform.position = anchor.position;
                    go.transform.rotation = Quaternion.Euler(0f, 90f + yawOffset, 0f);
                }
                currentStation[e.voter] = e.station;
            }
            else if (e.@event == "SERVICE_END")
            {
                inicioEstacion.Remove(e.voter);
                ReleaseSlot(e.voter);
                currentStation.Remove(e.voter);
            }
        }
    }

    // Dispara el efecto visual cuando arranca un evento externo (kind = corte_de_luz
    // | temblor | aguacero) y lo apaga cuando pasa su duracion. Los tres siguen
    // siendo funcionalmente identicos en el backend (mismo pause/resume) - la
    // diferencia vive solo aqui, en que hacemos con cada "kind".
    void ProcessExternalEvents()
    {
        var ev = timeline.external_events;
        if (ev == null) return;

        while (extIdx < ev.Count && ev[extIdx].t_start <= simClock)
        {
            var e = ev[extIdx++];
            // Un salto de reloj puede tragarse dos eventos de golpe; sin apagar el
            // anterior las luces se quedarian apagadas para siempre.
            if (eventoActivo != null) AplicarEvento(eventoActivo.kind, false);
            eventoActivo = e;
            finEventoActivo = e.t_start + e.duration;
            AplicarEvento(e.kind, true);
        }

        if (eventoActivo != null && simClock >= finEventoActivo)
        {
            AplicarEvento(eventoActivo.kind, false);
            eventoActivo = null;
        }
    }

    void AplicarEvento(string kind, bool activo)
    {
        switch (kind)
        {
            case "aguacero":
                if (lluviaVFX == null) break;
                if (activo) lluviaVFX.Play();
                else lluviaVFX.Stop();
                break;

            case "corte_de_luz":
                if (lucesCasilla != null) lucesCasilla.enabled = !activo;
                break;

            case "temblor":
                if (activo) StartCoroutine(Temblor());
                break;
        }
    }

    // duracionMin llega en minutos SIMULADOS (misma unidad que simClock);
    // lo convertimos a segundos reales usando 'speed' (min simulado / seg real).
    // La sacudida vive en el reloj simulado, no en segundos reales: pausar la
    // congela, x20 la acorta, y un salto que pase por encima la termina. De paso
    // desaparece la division entre speed, que reventaba con la velocidad en cero.
    IEnumerator Temblor()
    {
        float fin = finEventoActivo;
        while (simClock < fin)
        {
            if (camara != null) camara.sacudidaActual = shakeMagnitud;
            yield return null;
        }
        if (camara != null) camara.sacudidaActual = 0f;
    }

    void ProcessMovements()
    {
        var ev = timeline.movements;
        while (movIdx < ev.Count && ev[movIdx].t_start <= simClock)
        {
            currentMove[ev[movIdx].voter] = ev[movIdx];
            movIdx++;
        }
    }

    void InterpolateActiveMoves()
    {
        var terminados = new List<int>();
        foreach (var (id, m) in currentMove)
        {
            if (!voters.TryGetValue(id, out var go)) { terminados.Add(id); continue; }
            var a = AnchorFor(m.from, id);
            var b = AnchorFor(m.to, id);
            if (!a || !b) { terminados.Add(id); continue; }

            float u = Mathf.InverseLerp(m.t_start, m.t_end, simClock);
            Vector3 punto, dir;
            // Los que llegan recorren el zig-zag de la entrada en vez de cruzarlo
            // en linea recta.
            bool esLlegada = m.from.ToLowerInvariant() is "entrada" or "spawn";
            if (esLlegada && rutaEntrada.Count > 0)
                PuntoEnRutaEntrada(a.position, b.position, u, out punto, out dir);
            else
            {
                punto = Vector3.Lerp(a.position, b.position, u);
                dir = b.position - a.position;
            }
            go.transform.position = punto;
            dir.y = 0;
            if (dir.sqrMagnitude > 0.001f)
            {
                // LookRotation solo en horizontal, mas el offset del modelo.
                var objetivo = Quaternion.LookRotation(dir, Vector3.up) *
                               Quaternion.Euler(0f, yawOffset, 0f);
                go.transform.rotation = Quaternion.Slerp(
                    go.transform.rotation, objetivo, Time.deltaTime * 8f);
            }

            if (simClock >= m.t_end) terminados.Add(id);
        }
        foreach (var id in terminados) currentMove.Remove(id);
    }

    // Resuelve "secretario" a un SLOT concreto, "spawn" a SPAWN, "salida" a EXIT.
    Transform AnchorFor(string etapa, int voterId)
    {
        etapa = etapa.ToLowerInvariant();
        // El backend nombra el origen "entrada"; "spawn" se acepta por si acaso.
        if (etapa == "entrada" || etapa == "spawn") return Anchor("SPAWN");
        if (etapa == "salida") return Anchor("EXIT");
        int slot = voterSlot.TryGetValue(voterId, out var s) ? s : 0;
        return Anchor($"SLOT_{etapa.ToUpperInvariant()}_{slot:D2}");
    }

    Transform Anchor(string name)
    {
        anchors.TryGetValue(name.ToUpperInvariant(), out var t);
        return t;
    }

    // -------------------------------------------------------------------
    // Reparto de slots por estacion. El backend hoy no dice cual escritorio
    // ocupa cada votante, asi que aca damos round-robin sobre los libres.
    // Cuando Karlo agregue el slot al SERVICE_START, esto se quita.
    // -------------------------------------------------------------------
    int AssignSlot(int voterId, string etapa)
    {
        var pool = capacities[etapa];
        int start = nextSlot[etapa];
        for (int i = 0; i < pool.Length; i++)
        {
            int idx = (start + i) % pool.Length;
            if (pool[idx] == 0) { pool[idx] = voterId; nextSlot[etapa] = (idx + 1) % pool.Length; voterSlot[voterId] = idx; return idx; }
        }
        // Todos ocupados: sobreescribe el primero (no deberia pasar si el modelo cuadra).
        pool[0] = voterId; voterSlot[voterId] = 0; return 0;
    }

    void ReleaseSlot(int voterId)
    {
        if (!voterSlot.TryGetValue(voterId, out var idx)) return;
        foreach (var pool in capacities.Values)
            for (int i = 0; i < pool.Length; i++) if (pool[i] == voterId) pool[i] = 0;
        voterSlot.Remove(voterId);
    }
}
