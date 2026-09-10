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

    [Header("Eventos externos")]
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
    int movIdx, staIdx, votIdx, extIdx;
    ExternalEvent eventoActivo;
    float finEventoActivo;

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
        ProcessStationEvents();
        ProcessExternalEvents();
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
                ReleaseSlot(e.voter);
            }
        }
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

    // duracionMin llega en minutos SIMULADOS (misma unidad que simClock);
    // lo convertimos a segundos reales usando 'speed' (min simulado / seg real).
    IEnumerator Temblor(float duracionMin)
    {
        var camOriginal = Camera.main.transform.localPosition;
        float duracionReal = duracionMin * 60f / speed;
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
