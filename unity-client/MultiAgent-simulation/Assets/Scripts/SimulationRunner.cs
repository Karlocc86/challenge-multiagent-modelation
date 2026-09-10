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
    [System.Serializable] class VoterEvent { public int voter; public string @event; public float t; }

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
    int movIdx, staIdx, votIdx, colaIdx;

    void Start()
    {
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
        Debug.Log($"Timeline recibido: {timeline.movements.Count} movimientos, " +
                  $"{timeline.station_events.Count} eventos de estacion, " +
                  $"{timeline.voter_events.Count} eventos de votante.");
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

        // El reloj se detiene cuando termina la jornada (sale el ultimo votante).
        float fin = timeline.summary != null ? timeline.summary.duration_minutes : float.MaxValue;
        if (simClock >= fin)
        {
            simClock = fin;
            jornadaTerminada = true;
        }
        else
        {
            simClock += Time.deltaTime * (speed / 60f);   // speed = minutos simulados por segundo real
        }

        ProcessVoterEvents();
        ProcessQueueEvents();
        ProcessStationEvents();
        ProcessMovements();
        InterpolateActiveMoves();
        UpdateAnimators();
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

    GUIStyle _estilo, _estiloFin, _estiloGanador, _caja;
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
        }

        var res = timeline.summary != null ? timeline.summary.results : null;

        // El panel crece segun cuantos candidatos haya, para no cortar texto.
        int alto = 180;
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
            go.transform.position = Vector3.Lerp(a.position, b.position, u);
            var dir = (b.position - a.position); dir.y = 0;
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
        if (etapa == "spawn")  return Anchor("SPAWN");
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
