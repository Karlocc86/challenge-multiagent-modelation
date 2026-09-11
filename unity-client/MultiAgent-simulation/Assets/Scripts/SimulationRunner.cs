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
    // 469 = lista nominal 701 x participacion 66.9%, la casilla real que modela
    // este proyecto (PREP 2024, Seccion 3068 / Contigua 5, Puerta de Hierro /
    // Paseo Andares, Zapopan). Ver CANDIDATO_WEIGHTS en agents.py.
    public int numVoters = 469;

    // Realista = las llegadas varian con la hora (pico a media manana ~11:00 y un
    // repunte menor por la tarde, ventana 08:00-18:00). Uniforme = ritmo constante
    // (proceso de Poisson) usando 'Promedio Llegadas Por Minuto'.
    public enum PerfilLlegadas { Realista, Uniforme }

    [Tooltip("Realista: la afluencia cambia con la hora (pico a media manana). " +
             "Uniforme: ritmo constante segun 'Promedio Llegadas Por Minuto'.")]
    public PerfilLlegadas perfilLlegadas = PerfilLlegadas.Realista;

    // double, no float: Newtonsoft manda ~7 digitos para un float, y Python leeria
    // 0.3333333 en vez de 1/3, cambiando la corrida con la misma semilla.
    [Tooltip("Solo en perfil Uniforme. PROMEDIO de llegadas por minuto simulado; los " +
             "huecos entre llegadas siguen siendo aleatorios. 0.333 = una cada 3 min.")]
    // 0.78 = calibrado para que, con numVoters=469 (la casilla real), todos entren
    // antes de las 20:00 con margen. Solo aplica si perfilLlegadas = Uniforme.
    public double promedioLlegadasPorMinuto = 0.78;
    [Tooltip("Semilla del generador aleatorio. Con la misma semilla + mismos parametros " +
             "la corrida es identica. Se ignora si 'Semilla Aleatoria' esta activado.")]
    public int seed = 7;
    [Tooltip("Si esta activado, cada Play usa una semilla nueva al azar (resultados distintos " +
             "cada vez). La semilla usada se imprime en la consola y aparece en el dashboard.")]
    public bool semillaAleatoria = false;
    // Calibradas para que, con numVoters=469 y perfil Realista, la jornada cierre
    // cerca de la hora real (~17:48-19:46) con ~464-469 votos, como en la casilla real.
    public int secretarioCapacity = 2;
    public int mesaCapacity = 1;
    public int casillaCapacity = 3;
    public int urnaCapacity = 1;

    [Header("Formula Beta de llegadas")]
    [Tooltip("Usa 0.82 Beta(2,4) + 0.18 Beta(6,2) entre Hora Inicio y Hora Fin Beta.")]
    public bool usarFormulaBeta = true;
    [Range(1, 24)] public int horaFinBeta = 18;

    [Header("Prefabs y reproduccion")]
    public GameObject prefabHombre;
    public GameObject prefabMujer;
    [Tooltip("Velocidad inicial de reproduccion: segundos simulados por segundo real. " +
             "x1 = tiempo real. Se puede cambiar en vivo con los botones del panel o las teclas 1-5.")]
    [SerializeField] float multiplicadorInicial = 1f;
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
             "y 10). A x64, 20 aqui son ~19 segundos reales.")]
    public double duracionDelEvento = 0.0;

    [Tooltip("Particle System de lluvia. Colocalo sobre el patio/entrada, con 'Play On Awake' desactivado.")]
    public ParticleSystem lluviaVFX;
    [Tooltip("Luz(es) que se apagan durante un corte_de_luz.")]
    public Light lucesCasilla;
    [Tooltip("Que tanto se sacude la camara durante un temblor.")]
    public float shakeMagnitud = 0.15f;

    [Header("Panel en pantalla")]
    [Tooltip("Hora simulada a la que arranca la jornada (24h). 8 = 8:00 AM.")]
    public int horaInicio = 8;
    [Tooltip("Mostrar el panel con reloj y contadores.")]
    public bool mostrarPanel = true;
    [Tooltip("Mostrar una grafica sencilla con las llegadas de cada hora.")]
    public bool mostrarGraficaLlegadas = true;

    // Contadores en vivo (se recalculan cada frame desde los eventos ya ocurridos).
    int llegados, atendidos, rechazados;
    bool jornadaTerminada;
    int[] llegadasPorHora;
    int maxLlegadasEnUnaHora;

    // Reproduccion: multiplicador = segundos simulados por segundo real. x1 = tiempo
    // real (un minuto simulado tarda 60 s reales). Se ajusta en vivo desde el panel
    // o con las teclas 1-5; `pausado` congela el reloj sin perder el estado.
    static readonly float[] MULTIPLICADORES = { 1f, 2f, 4f, 16f, 64f };
    static readonly Key[] TECLAS_VELOCIDAD =
        { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5 };
    float multiplicadorVelocidad = 1f;
    bool pausado;
    bool confirmandoFin;   // el boton "Terminar jornada" espera un segundo clic

    // Respaldo si el backend no manda summary.jornada_minutes (8:00 AM -> 8:00 PM).
    const float JORNADA_MINUTOS_DEFAULT = 720f;

    // Minuto simulado en que se cierra la entrada. El reloj y el procesamiento
    // siguen despues de esto: la jornada solo termina cuando sale la ultima
    // persona (simClock >= duration_minutes).
    float JornadaMinutos =>
        timeline != null && timeline.summary != null && timeline.summary.jornada_minutes > 0f
            ? timeline.summary.jornada_minutes
            : JORNADA_MINUTOS_DEFAULT;

    // Entrada cerrada (ya son las 20:00) pero todavia queda gente adentro.
    bool RecepcionCerrada => !jornadaTerminada && simClock >= JornadaMinutos;

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
        // Salida de la ultima persona, NO las 8:00 PM: si alguien admitido antes
        // del cierre sigue en fila, la corrida termina despues del minuto 720.
        public float duration_minutes;
        // Largo de la jornada en minutos simulados (8:00 AM -> 8:00 PM = 720).
        // A esa hora se cierra la entrada; el procesamiento sigue. 0 en un
        // backend viejo que no lo mande -> se usa el respaldo de 720.
        public float jornada_minutes;
        public int voters_arrived;
        public int voters_exited;
        public int voters_rejected;
        public Results results;
        public Newtonsoft.Json.Linq.JObject arrival_beta;
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
    [System.Serializable] class VoterEvent { public int voter; public string @event; public float t; }
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
    readonly Dictionary<int, float> despawnAt = new();      // id -> minuto sim en que se destruye (ya saliendo)
    readonly Dictionary<int, Vector3> salidaDesde = new();  // id -> pos inicial de una salida sintetica (rechazo)
    const float SALIDA_RECHAZO_MIN = 0.4f;                  // duracion (min sim) de la caminata de salida de un rechazado
    // Zig-zag de la entrada: QUEUE_GENERAL_000..045, en orden. Las distancias
    // acumuladas se calculan una vez porque las anclas no se mueven.
    readonly List<Vector3> rutaEntrada = new();
    readonly List<float> distanciaEnRuta = new();
    int movIdx, staIdx, votIdx, colaIdx, extIdx;
    ExternalEvent eventoActivo;
    float finEventoActivo;

    void Start()
    {
        multiplicadorVelocidad = multiplicadorInicial > 0f ? multiplicadorInicial : 1f;

        if (semillaAleatoria)
        {
            // Derivada del reloj (cambia cada ~100 ns), NO de UnityEngine.Random:
            // con "Reload Domain" desactivado en el Editor, Random no se reinicializa
            // entre Plays y devolveria la misma "semilla" hasta cerrar Unity.
            seed = (int)(System.DateTime.UtcNow.Ticks & 0x7FFFFFFF);
            if (seed == 0) seed = 1;
            Debug.Log($"Semilla aleatoria de esta corrida: {seed}");
        }

        capacities["secretario"] = new int[secretarioCapacity];
        capacities["mesa"]       = new int[mesaCapacity];
        capacities["casilla"]    = new int[casillaCapacity];
        capacities["urna"]       = new int[urnaCapacity];
        foreach (var etapa in capacities.Keys) nextSlot[etapa] = 0;

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

    // Ruta de llegada: pasillo exterior PATH_ACCESO_000.. (calle -> puerta) seguido
    // del zig-zag QUEUE_GENERAL_000.., en el orden en que se recorren. La guardamos
    // como una polilinea con sus distancias acumuladas para interpolar a lo largo de
    // ella a velocidad pareja.
    void ConstruirRutaEntrada()
    {
        rutaEntrada.Clear();
        distanciaEnRuta.Clear();
        for (int i = 0; ; i++)
        {
            var t = Anchor($"PATH_ACCESO_{i:D3}");
            if (!t) break;
            rutaEntrada.Add(t.position);
        }
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
        object formulaBeta = usarFormulaBeta ? new
        {
            start_hour = horaInicio,
            end_hour = horaFinBeta,
            components = new[]
            {
                new { weight = 0.82, alpha = 2.0, beta = 4.0 },
                new { weight = 0.18, alpha = 6.0, beta = 2.0 }
            }
        } : null;
        var body = JsonConvert.SerializeObject(new
        {
            num_voters = numVoters,
            arrival_rate = promedioLlegadasPorMinuto,
            arrival_profile = perfilLlegadas == PerfilLlegadas.Uniforme ? "uniforme" : "realista",
            arrival_beta = formulaBeta,
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
        if (usarFormulaBeta && timeline?.summary?.arrival_beta == null)
        {
            timeline = null;
            Debug.LogError("El servidor no reconoce la formula Beta. Ejecuta backend/server.py de esta copia.");
            yield break;
        }
        Debug.Log($"Timeline recibido: {timeline.movements.Count} movimientos, " +
                  $"{timeline.station_events.Count} eventos de estacion, " +
                  $"{timeline.voter_events.Count} eventos de votante.");
        PrepararLlegadasPorHora();
    }

    // El inspector usa nombres legibles; el contrato del backend usa los suyos.
    static string KindDeEvento(EventoExternoForzado e) => e switch
    {
        EventoExternoForzado.CorteDeLuz => "corte_de_luz",
        EventoExternoForzado.Temblor    => "temblor",
        EventoExternoForzado.Aguacero   => "aguacero",
        _                               => null,
    };

    // Agrupa una sola vez los ARRIVAL que ya entrego el backend. La grafica usa
    // estos resultados completos; Speed solo cambia la reproduccion visual.
    void PrepararLlegadasPorHora()
    {
        if (timeline?.voter_events == null) return;
        float ultimaLlegada = 0f;
        foreach (var e in timeline.voter_events)
            if (e.@event == "ARRIVAL") ultimaLlegada = Mathf.Max(ultimaLlegada, e.t);

        int horas = usarFormulaBeta
            ? horaFinBeta - horaInicio
            : Mathf.Max(1, Mathf.CeilToInt(ultimaLlegada / 60f));
        if (horas <= 0) return;
        llegadasPorHora = new int[horas];
        foreach (var e in timeline.voter_events)
        {
            if (e.@event != "ARRIVAL") continue;
            int indice = Mathf.FloorToInt(e.t / 60f);
            if (0 <= indice && indice < llegadasPorHora.Length) llegadasPorHora[indice]++;
        }
        maxLlegadasEnUnaHora = llegadasPorHora.Max();

        if (usarFormulaBeta)
            Debug.Log("Formula Beta confirmada: 0.82 Beta(2,4) + 0.18 Beta(6,2), " +
                      $"horario {horaInicio:00}:00-{horaFinBeta:00}:00.");
        for (int i = 0; i < llegadasPorHora.Length; i++)
            Debug.Log($"Llegadas {horaInicio + i:00}:00-{horaInicio + i + 1:00}:00: {llegadasPorHora[i]}");
    }

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

        LeerAtajosDeTeclado();

        // El reloj se detiene cuando termina la jornada (sale el ultimo votante).
        float fin = timeline.summary != null ? timeline.summary.duration_minutes : float.MaxValue;
        if (simClock >= fin)
        {
            simClock = fin;
            jornadaTerminada = true;
        }
        else if (!pausado)
        {
            // multiplicadorVelocidad = segundos simulados por segundo real; /60 -> minutos.
            simClock += Time.deltaTime * (multiplicadorVelocidad / 60f);
        }

        ProcessVoterEvents();
        ProcessQueueEvents();
        ProcessStationEvents();
        ProcessExternalEvents();
        ProcessMovements();
        InterpolateActiveMoves();
        BarrerVotantesQueSalieron();
        UpdateAnimators();
    }

    // Destruye a los votantes cuya caminata de salida ya termino (o al cerrar la
    // jornada, para no dejar a nadie a medio camino). Se mantienen vivos durante la
    // salida para que la animacion de caminado se reproduzca.
    void BarrerVotantesQueSalieron()
    {
        if (despawnAt.Count == 0) return;

        var fuera = new List<int>();
        foreach (var (id, t) in despawnAt)
            if (jornadaTerminada || simClock >= t) fuera.Add(id);

        foreach (var id in fuera)
        {
            if (voters.TryGetValue(id, out var go) && go) Destroy(go);
            voters.Remove(id);
            currentMove.Remove(id);
            despawnAt.Remove(id);
            salidaDesde.Remove(id);
            voterSlot.Remove(id);
        }
    }

    // Teclas 1-5 = x1/x2/x4/x16/x64, Espacio = pausa/reanuda, N o flecha derecha =
    // salta a la siguiente llegada. Nuevo Input System: el legacy UnityEngine.Input
    // esta deshabilitado en este proyecto.
    void LeerAtajosDeTeclado()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        for (int i = 0; i < TECLAS_VELOCIDAD.Length; i++)
            if (kb[TECLAS_VELOCIDAD[i]].wasPressedThisFrame)
                multiplicadorVelocidad = MULTIPLICADORES[i];

        if (kb[Key.Space].wasPressedThisFrame)
            pausado = !pausado;

        if (kb[Key.N].wasPressedThisFrame || kb[Key.RightArrow].wasPressedThisFrame)
            SaltarASiguienteLlegada();
    }

    // Minuto de la proxima llegada (ARRIVAL) por delante de simClock, o -1 si ya no
    // quedan llegadas (todos entraron / recepcion cerrada).
    float MinutoSiguienteLlegada()
    {
        if (timeline == null || timeline.voter_events == null) return -1f;
        foreach (var e in timeline.voter_events)
            if (e.@event == "ARRIVAL" && e.t > simClock + 1e-4f)
                return e.t;
        return -1f;
    }

    // Adelanta el reloj hasta la proxima llegada. Los cursores monotonos de los
    // Process*() drenan todo lo intermedio en el siguiente frame. Se detiene antes
    // si en el camino empieza (o esta en curso) un evento externo, para que su
    // efecto visual se active/desactive limpio.
    void SaltarASiguienteLlegada()
    {
        if (timeline == null || jornadaTerminada) return;

        float destino = MinutoSiguienteLlegada();
        if (destino < 0f) return;

        float fin = timeline.summary != null ? timeline.summary.duration_minutes : float.MaxValue;
        destino = Mathf.Min(destino, fin);

        if (timeline.external_events != null)
        {
            foreach (var ev in timeline.external_events)
            {
                float finEv = ev.t_start + ev.duration;
                if (finEv <= simClock) continue;                     // ya paso entero
                if (ev.t_start > simClock && ev.t_start < destino)
                    destino = ev.t_start;                            // parar en su inicio
                else if (ev.t_start <= simClock)
                    destino = Mathf.Min(destino, finEv);             // en curso: parar al terminar
            }
        }

        if (destino > simClock) simClock = destino;
    }

    // Salta al final real de la corrida. El siguiente Update() lo resuelve por el
    // camino normal de fin de jornada: fija jornadaTerminada, los Process*() drenan
    // lo que quede y BarrerVotantesQueSalieron() destruye a los votantes restantes.
    void TerminarSimulacion()
    {
        if (timeline == null || jornadaTerminada) return;
        float fin = timeline.summary != null ? timeline.summary.duration_minutes : simClock;
        simClock = Mathf.Max(simClock, fin);
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

    GUIStyle _estilo, _estiloFin, _estiloGanador, _estiloCerrada, _boton, _caja,
             _estiloGrafica, _estiloGraficaPequeno, _estiloGraficaCentro;
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
            _estiloCerrada = new GUIStyle(_estilo)
            {
                fontSize = 22,
                normal = { textColor = new Color(1f, 0.75f, 0.25f) }   // ambar
            };
            _boton = new GUIStyle(GUI.skin.button)
            {
                font = _fuente, fontSize = 15, richText = true,
                padding = new RectOffset(4, 4, 4, 4)
            };
            _estiloGrafica = new GUIStyle(_estilo) { fontSize = 18 };
            _estiloGraficaPequeno = new GUIStyle(_estilo) { fontSize = 13 };
            _estiloGraficaCentro = new GUIStyle(_estiloGraficaPequeno)
            {
                alignment = TextAnchor.UpperCenter
            };
        }

        var res = timeline.summary != null ? timeline.summary.results : null;

        // El panel crece segun cuantos candidatos haya, para no cortar texto.
        int alto = 180;
        alto += 76;   // dos filas de controles de reproduccion
        if (!jornadaTerminada) alto += 30;   // fila "Terminar jornada"
        if (RecepcionCerrada) alto += 34;
        if (jornadaTerminada)
        {
            alto += 40;
            if (res != null && res.votes_by_candidate != null)
                alto += 34 + res.votes_by_candidate.Count * 24 + 34;
        }

        GUI.Box(new Rect(20, 20, 300, alto), GUIContent.none, _caja);
        GUILayout.BeginArea(new Rect(34, 30, 280, alto - 10));
        GUILayout.Label("<b>Casilla Especial - Andares</b>", _estilo);
        GUILayout.Space(6);
        GUILayout.Label($"Hora:  <b>{RelojSimulado()}</b>", _estilo);
        GUILayout.Space(10);
        GUILayout.Label($"Llegaron:   <b>{llegados}</b>", _estilo);
        GUILayout.Label($"Votaron:    <b>{atendidos}</b>", _estilo);
        GUILayout.Label($"Rechazados: <b>{rechazados}</b>", _estilo);

        DibujarControlesReproduccion();

        // Ya son las 20:00 (o mas) y todavia hay gente adentro. El reloj sigue
        // corriendo; la jornada NO termina solo porque el reloj llegue aca.
        if (RecepcionCerrada)
        {
            GUILayout.Space(8);
            GUILayout.Label("RECEPCIÓN CERRADA", _estiloCerrada);
        }

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
        if (jornadaTerminada)
            DibujarGraficaLlegadas();
    }

    void DibujarGraficaLlegadas()
    {
        if (!mostrarGraficaLlegadas || llegadasPorHora == null ||
            llegadasPorHora.Length == 0 || maxLlegadasEnUnaHora <= 0) return;

        float ancho = Mathf.Min(760f, Screen.width - 360f);
        if (ancho < 400f) return;
        const float alto = 330f;
        float x = Screen.width - ancho - 20f;
        float y = 20f;
        GUI.Box(new Rect(x, y, ancho, alto), GUIContent.none, _caja);
        GUI.Label(new Rect(x + 15, y + 10, ancho - 30, 26),
                  "<b>Llegadas por hora (corrida completa)</b>", _estiloGrafica);

        string modo = usarFormulaBeta
            ? $"Beta 08:00-{horaFinBeta:00}:00"
            : $"Poisson {promedioLlegadasPorMinuto:0.##}/min";
        GUI.Label(new Rect(x + 15, y + 38, ancho - 30, 22),
                  $"Modo: {modo}   |   Semilla: {seed}   |   Personas: {numVoters}",
                  _estiloGraficaPequeno);
        GUI.Label(new Rect(x + 15, y + 58, ancho - 30, 22),
                  $"Capacidades S/M/C/U: {secretarioCapacity}/{mesaCapacity}/{casillaCapacity}/{urnaCapacity}",
                  _estiloGraficaPequeno);

        string evento = "Evento externo: ninguno";
        if (timeline.external_events != null && timeline.external_events.Count > 0)
        {
            var e = timeline.external_events[0];
            evento = $"Evento externo: {e.kind.Replace('_', ' ')} a las " +
                     $"{HoraCorta(e.t_start)} ({e.duration:0.0} min)";
        }
        GUI.Label(new Rect(x + 15, y + 78, ancho - 30, 22), evento, _estiloGraficaPequeno);

        float graficaX = x + 35f;
        float graficaY = y + 112f;
        float graficaAncho = ancho - 55f;
        const float graficaAlto = 165f;
        Color colorAnterior = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.45f);
        GUI.DrawTexture(new Rect(graficaX, graficaY + graficaAlto, graficaAncho, 2f),
                        Texture2D.whiteTexture);

        float espacio = graficaAncho / llegadasPorHora.Length;
        float barraAncho = Mathf.Max(4f, espacio * 0.68f);
        for (int i = 0; i < llegadasPorHora.Length; i++)
        {
            float barraAlto = graficaAlto * llegadasPorHora[i] / maxLlegadasEnUnaHora;
            float barraX = graficaX + i * espacio + (espacio - barraAncho) / 2f;
            float barraY = graficaY + graficaAlto - barraAlto;
            GUI.color = new Color(0.18f, 0.75f, 1f, 0.92f);
            GUI.DrawTexture(new Rect(barraX, barraY, barraAncho, barraAlto),
                            Texture2D.whiteTexture);
            GUI.color = colorAnterior;
            GUI.Label(new Rect(barraX - 6f, barraY - 19f, barraAncho + 12f, 18f),
                      llegadasPorHora[i].ToString(), _estiloGraficaCentro);
            GUI.Label(new Rect(graficaX + i * espacio, graficaY + graficaAlto + 5f,
                               espacio, 20f),
                      $"{(horaInicio + i) % 24:00}", _estiloGraficaCentro);
        }
        GUI.color = colorAnterior;
        GUI.Label(new Rect(graficaX, graficaY + graficaAlto + 27f, graficaAncho, 20f),
                  "Hora de inicio de cada intervalo", _estiloGraficaCentro);
    }

    string HoraCorta(float minutosDesdeInicio)
    {
        int total = horaInicio * 60 + Mathf.FloorToInt(minutosDesdeInicio);
        return $"{(total / 60) % 24:00}:{total % 60:00}";
    }

    // Fila de velocidades (x1..x64), pausa y salto a la siguiente llegada. Los
    // mismos controles que las teclas 1-5 / Espacio / N.
    void DibujarControlesReproduccion()
    {
        GUILayout.Space(10);

        GUILayout.BeginHorizontal();
        for (int i = 0; i < MULTIPLICADORES.Length; i++)
        {
            bool activo = Mathf.Approximately(multiplicadorVelocidad, MULTIPLICADORES[i]);
            string etiqueta = activo ? $"<b>x{MULTIPLICADORES[i]:0}</b>" : $"x{MULTIPLICADORES[i]:0}";
            var fondo = GUI.backgroundColor;
            if (activo) GUI.backgroundColor = new Color(0.45f, 1f, 0.55f);
            if (GUILayout.Button(etiqueta, _boton))
                multiplicadorVelocidad = MULTIPLICADORES[i];
            GUI.backgroundColor = fondo;
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(4);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(pausado ? "Reanudar" : "Pausa", _boton))
            pausado = !pausado;

        bool hayLlegada = !jornadaTerminada && MinutoSiguienteLlegada() >= 0f;
        GUI.enabled = hayLlegada;
        if (GUILayout.Button("Siguiente llegada >>", _boton))
            SaltarASiguienteLlegada();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        // Terminar de golpe: salta al final real de la corrida. Pide un segundo clic.
        if (!jornadaTerminada)
        {
            GUILayout.Space(4);
            if (!confirmandoFin)
            {
                if (GUILayout.Button("Terminar jornada", _boton))
                    confirmandoFin = true;
            }
            else
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Confirmar terminar", _boton))
                {
                    TerminarSimulacion();
                    confirmandoFin = false;
                }
                if (GUILayout.Button("Cancelar", _boton))
                    confirmandoFin = false;
                GUILayout.EndHorizontal();
            }
        }
    }

    // Tope del multiplicador para la animacion: de aqui en adelante el ciclo de
    // piernas queda en "caminata rapida" en vez de volverse un borron a x16/x64.
    const float ANIM_SPEED_MAX = 4f;

    // Enciende "Caminando" en quien tiene un movimiento activo, lo apaga en los
    // demas, y ata el ritmo del ciclo de piernas al reloj global (con tope) para
    // que los modelos no "patinen" al subir la velocidad. En pausa se congela.
    void UpdateAnimators()
    {
        float animSpeed = pausado ? 0f : Mathf.Min(multiplicadorVelocidad, ANIM_SPEED_MAX);
        foreach (var (id, go) in voters)
        {
            if (go == null) continue;
            var anim = go.GetComponentInChildren<Animator>();
            if (anim == null) continue;
            anim.SetBool("Caminando", currentMove.ContainsKey(id));
            anim.speed = animSpeed;
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
                var spawn = Anchor("SPAWN_EXTERIOR") ?? Anchor("SPAWN");   // desde la calle, no la puerta
                var go = Instantiate(prefab, spawn ? spawn.position : Vector3.zero, Quaternion.identity, transform);
                go.name = $"Votante_{e.voter}";
                voters[e.voter] = go;
            }
            else if (e.@event == "EXIT" || e.@event == "REJECTED")
            {
                IniciarSalida(e.voter, e.@event);
            }
        }
    }

    // Arranca la caminata de salida de un votante. Libera ya sus recursos (fila,
    // slot, contador) pero lo mantiene vivo hasta que llega a la puerta:
    //  - EXIT: el backend ya manda un MOVE urna->salida; ProcessMovements lo va a
    //    reproducir este mismo frame. Solo apuntamos cuando destruirlo.
    //  - REJECTED: no hay MOVE en el timeline; sintetizamos una caminata de vuelta
    //    hacia la entrada desde donde esta parado.
    void IniciarSalida(int id, string evento)
    {
        if (evento == "EXIT") atendidos++;
        else rechazados++;

        currentStation.Remove(id);
        SoltarLugarEnFila(id);
        ReleaseSlot(id);

        if (evento == "EXIT")
        {
            float tEnd = simClock + SALIDA_RECHAZO_MIN;   // respaldo si no aparece el tramo
            if (timeline.movements != null)
                foreach (var m in timeline.movements)
                    if (m.voter == id && m.from == "urna" && m.to == "salida")
                    {
                        tEnd = m.t_end;
                        break;
                    }
            despawnAt[id] = tEnd;
            return;
        }

        // REJECTED: caminata sintetica de regreso a la entrada.
        if (voters.TryGetValue(id, out var go) && go)
            salidaDesde[id] = go.transform.position;
        currentMove[id] = new Movement
        {
            voter = id, from = "__rechazo__", to = "entrada",
            t_start = simClock, t_end = simClock + SALIDA_RECHAZO_MIN
        };
        despawnAt[id] = simClock + SALIDA_RECHAZO_MIN;
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
                if (activo) StartCoroutine(Temblor(finEventoActivo - simClock));
                break;
        }
    }

    // duracionMin llega en minutos SIMULADOS (misma unidad que simClock); lo
    // convertimos a segundos reales con la velocidad de reproduccion actual
    // (multiplicadorVelocidad = segundos simulados por segundo real). Si se cambia
    // la velocidad a mitad del temblor no se reajusta: dura lo que se calculo aqui.
    IEnumerator Temblor(float duracionMin)
    {
        var camOriginal = Camera.main.transform.localPosition;
        float velocidad = Mathf.Max(0.01f, multiplicadorVelocidad);
        float duracionReal = duracionMin * 60f / velocidad;
        float t = 0f;
        while (t < duracionReal)
        {
            Camera.main.transform.localPosition =
                camOriginal + (Vector3)(Random.insideUnitCircle * shakeMagnitud);
            t += Time.deltaTime;
            yield return null;
        }
        Camera.main.transform.localPosition = camOriginal;
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

            // Origen: una salida sintetica (rechazo) arranca desde la posicion guardada.
            Vector3 aPos;
            if (salidaDesde.TryGetValue(id, out var desdePos))
                aPos = desdePos;
            else
            {
                var a = AnchorFor(m.from, id);
                if (!a) { terminados.Add(id); continue; }
                aPos = a.position;
            }

            // Destino: si va a una estacion y ya tiene lugar de fila asignado, terminar
            // el tramo exactamente en ese lugar (evita el salto escritorio -> fila).
            Vector3 bPos;
            if (EsEstacion(m.to) && anclaDeEspera.TryGetValue(id, out var qa) && qa)
                bPos = qa.position;
            else
            {
                var b = AnchorFor(m.to, id);
                if (!b) { terminados.Add(id); continue; }
                bPos = b.position;
            }

            float u = Mathf.InverseLerp(m.t_start, m.t_end, simClock);
            Vector3 punto, dir;
            // Los que llegan recorren el zig-zag de la entrada en vez de cruzarlo
            // en linea recta.
            bool esLlegada = m.from.ToLowerInvariant() is "entrada" or "spawn";
            if (esLlegada && rutaEntrada.Count > 0)
                PuntoEnRutaEntrada(aPos, bPos, u, out punto, out dir);
            else
            {
                punto = Vector3.Lerp(aPos, bPos, u);
                dir = bPos - aPos;
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

    // Resuelve "secretario" a un SLOT concreto, "entrada"/"spawn" a la calle
    // (SPAWN_EXTERIOR), "salida" a EXIT.
    Transform AnchorFor(string etapa, int voterId)
    {
        etapa = etapa.ToLowerInvariant();
        // El backend nombra el origen "entrada"; "spawn" se acepta por si acaso.
        if (etapa == "entrada" || etapa == "spawn") return Anchor("SPAWN_EXTERIOR") ?? Anchor("SPAWN");
        if (etapa == "salida") return Anchor("EXIT");
        int slot = voterSlot.TryGetValue(voterId, out var s) ? s : 0;
        return Anchor($"SLOT_{etapa.ToUpperInvariant()}_{slot:D2}");
    }

    // true si `etapa` es una de las cuatro estaciones con fila.
    bool EsEstacion(string etapa) => capacities.ContainsKey(etapa.ToLowerInvariant());

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