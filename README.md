# Simulación multiagente de una casilla INE

Simulación de eventos discretos que reproduce el flujo de votantes dentro de
una casilla electoral mexicana. El backend está construido con **Python y
Mesa**: genera llegadas mediante procesos estocásticos calibrados, modela
estaciones con capacidad limitada y colas de espera, y avanza un reloj
simulado saltando directamente entre eventos (sin ticks fijos). El frontend
es una **escena de Unity** que reproduce visualmente la línea de tiempo que
el backend calcula de antemano, más un **dashboard HTML** autocontenido para
presentar resultados sin abrir Unity.

## Fuente de datos real: la casilla que este proyecto modela

La estadística, las proporciones de llegada y el reparto de votos **no son
inventados**: están calibrados contra los resultados oficiales de una casilla
real de la elección presidencial de México 2024, publicados por el INE en el
Programa de Resultados Electorales Preliminares (PREP):

> **Casilla:** Sección 3068, Casilla Contigua 5 — Distrito 10, Entidad 14
> (Jalisco) — Escuela Primaria Urbana Federal "Calmecac", Puerta de Hierro /
> Paseo Andares, Zapopan, Jal.
> **Fuente:** <https://prep2024.ine.mx/publicacion/nacional/presidencia/nacional/entidad/14/distrito/10/seccion/3068/casilla/5>

De esa casilla se tomaron tres calibraciones concretas, todas ubicadas en
[`backend/server.py`](backend/server.py), [`backend/casilla/agents.py`](backend/casilla/agents.py)
y [`backend/casilla/model.py`](backend/casilla/model.py):

| Dato real (PREP) | Valor | Dónde se usa en el código |
|---|---|---|
| Lista nominal (electores esperados) | 701 | `LISTA_NOMINAL_REAL` en `server.py` |
| Participación real | 66.9 % | `PARTICIPACION_REAL` en `server.py` |
| Votantes que sí llegaron a votar | 469 (701 × 66.9 %) | `num_voters` por defecto del escenario de la API/Unity |
| Votos por Xóchitl Gálvez (PAN-PRI-PRD) | 354 | `CANDIDATO_WEIGHTS` en `agents.py` |
| Votos por Jorge Álvarez Máynez (MC) | 51 | `CANDIDATO_WEIGHTS` en `agents.py` |
| Votos por Claudia Sheinbaum (Morena-PT-PVEM) | 50 | `CANDIDATO_WEIGHTS` en `agents.py` |
| Hora de cierre real de la jornada | ~17:48–19:46 | capacidades por estación calibradas en `DEFAULT_SCENARIO` |

Notas sobre estas calibraciones:

- El modelo no tiene un concepto de "elector esperado que nunca llegó", así
  que `num_voters` representa a los **469 que sí votaron**, no a los 701 de
  la lista nominal. `701 × 0.669 ≈ 469`.
- Los votos nulos y de candidaturas no registradas (~2 % del total real) no
  tienen categoría propia en el modelo y se excluyen del sorteo; los tres
  pesos de `CANDIDATO_WEIGHTS` son los conteos reales tal cual, sin
  normalizar a mano — `random.choices()` los normaliza internamente.
- Las capacidades por estación (`secretario_capacity=2`, `mesa_capacity=1`,
  `casilla_capacity=3`, `urna_capacity=1`) y la curva de llegadas ("realista",
  ver abajo) se ajustaron para que una corrida con semilla `7` cierre
  aproximadamente a la misma hora que la casilla real y con un conteo final
  de votos muy cercano a 469.
- Cambiar `seed`, `num_voters` u otros parámetros vía la API o el inspector
  de Unity aleja la corrida del caso real calibrado; para reproducir el
  escenario documentado usa los valores por defecto (`seed=7`,
  `num_voters=469`, perfil `realista`).

## Objetivos

- Representar el recorrido `entrada -> secretario -> mesa -> casilla -> urna -> salida`.
- Modelar tiempos de servicio, traslados y llegadas en minutos simulados.
- Aplicar capacidad configurable por estación y colas de espera (con
  prioridad para adultos mayores).
- Representar comunicación entre agentes mediante mensajes explícitos.
- Calibrar la afluencia y el resultado electoral contra una casilla real del
  PREP 2024, en vez de usar cifras arbitrarias.
- Exponer la simulación vía HTTP para que Unity la reproduzca visualmente y
  un dashboard la resuma sin volver a calcularla.

## Arquitectura

```text
                    +-----------------------------+
                    |  Mesa: reloj y event queue   |
                    +--------------+----------------+
                                   |
                                   v
       +---------------------------+---------------------------+
       |            CasillaModel (backend/casilla/)             |
       |                                                         |
       |  VoterAgent -> secretario -> mesa -> casilla -> urna   |
       |                    ^                         |          |
       |                    |                         v          |
       |              Coordinador <--- evento externo       event_log
       +---------------------------------------------------------+
                                   |
                                   v
                    build_timeline() (backend/casilla/timeline.py)
                                   |
                    +--------------+---------------+
                    v                               v
         Flask API (backend/server.py)     pytest / consola (main.py)
           POST /simulate  -> JSON                  |
           GET  /          -> dashboard.html        v
                    |                          logs de consola
                    v
        Unity (SimulationRunner.cs) reproduce
        la línea de tiempo ya calculada
```

## Flujo de la simulación

```text
Llegada de votantes (perfil "realista": mezcla Beta calibrada a la
casilla real | perfil "uniforme": Poisson con --arrival-rate)
        |
        v
  secretario --TURN/WAIT--> mesa --TURN/WAIT--> casilla --TURN/WAIT--> urna --> salida
   (1.5-2.5 min)   |      (0.5-1.5 min)        (2.0-4.0 min)        (0.2-0.6 min)
   cada estación: capacidad configurable, colas y tiempo de servicio uniforme
                   | --rejection-rate (default 2%)
                   v
              REJECTED --> el votante abandona el sistema

Evento externo (corte_de_luz | temblor | aguacero)
        |
        v
  Coordinador --PAUSE--> secretario, mesa, casilla, urna
        | (duración aleatoria o forzada)
        v
  Coordinador --RESUME--> secretario, mesa, casilla, urna
```

- `CasillaModel` usa el scheduler de eventos nativo de Mesa y no un ciclo de
  ticks fijos. `run_to_completion()` procesa la cola hasta que no quedan
  eventos pendientes.
- Cada estación (`Station`) tiene capacidad configurable, tiempo de servicio
  aleatorio y dos colas: una prioritaria para adultos mayores (60+) y otra
  regular. Cada cola conserva el orden de llegada.
- Los votantes (`VoterAgent`) reaccionan a mensajes `TURN`, `WAIT` y
  `REJECTED`. Las estaciones y el coordinador también se comunican mediante
  objetos `Message`.
- A cada votante se le asigna un voto (`PAN`, `Movimiento Ciudadano` o
  `Morena`) al llegar, con probabilidad proporcional a `CANDIDATO_WEIGHTS`
  (los votos reales de la casilla modelada). El voto solo se cuenta como
  emitido cuando el votante sale por la urna; uno rechazado en el secretario
  o que sigue dentro cuando termina la corrida no lo deposita.
- Después del secretario, la INE puede rechazarse según `--rejection-rate`.
  El votante rechazado no continúa hacia las estaciones restantes.
- Hay dos perfiles de llegada: `realista` (mezcla de dos Beta, ver
  [FORMULA_BETA.md](FORMULA_BETA.md)) que reproduce la curva de afluencia
  típica de una jornada electoral, y `uniforme` (Poisson clásico,
  `--arrival-rate`).
- Las llegadas dejan de admitirse a las 20:00 simuladas
  (`JORNADA_MINUTOS`); quienes ya estaban dentro terminan su recorrido
  aunque la hora de cierre ya haya pasado.
- En cada ejecución se programa un evento externo (`corte_de_luz`,
  `temblor` o `aguacero`), salvo que se fuerce uno específico o se desactive.
  El coordinador pausa las cuatro estaciones y las reanuda al terminar la
  duración del evento.
- `event_log` conserva llegadas, movimientos, filas, terminaciones, rechazos,
  salidas y el evento externo. Los detalles de comunicación se emiten
  mediante `logging`.

Todas las duraciones están expresadas en **minutos simulados**. Los
movimientos se registran como segmentos con tiempo inicial y final; Unity
interpola esos segmentos para animar a cada votante entre estaciones.

## Estructura del proyecto

```text
backend/
  casilla/
    __init__.py               Exporta CasillaModel
    model.py                  Modelo, scheduler, perfiles de llegada y coordinación del flujo
    agents.py                 VoterAgent, Station, Coordinador, Message y CANDIDATO_WEIGHTS
    arrivals.py                Validación y muestreo de la mezcla Beta de llegadas
    timeline.py                Construye el JSON de salida (summary, movements, eventos, resultados)
  tests/
    conftest.py                Configuración de importación para pytest
    test_casilla_model.py      Suite del scheduler, flujo y agentes
    test_beta_arrivals.py      Validación y muestreo de la mezcla Beta
    test_server.py             Endpoints /simulate y / (dashboard)
    test_timeline.py           Forma y consistencia del JSON de línea de tiempo
  static/
    dashboard.html              Dashboard autocontenido servido en GET /
  main.py                       Punto de entrada de la demo de consola
  server.py                     API Flask (/simulate, /) y escenario calibrado a la casilla real
  requirements.txt              Dependencias de Python
client-csharp/
  Program.cs                    Cliente HTTP C# de referencia para consultar la API
unity-client/
  MultiAgent-simulation/        Proyecto Unity
    Assets/Scripts/
      SimulationRunner.cs       Consulta POST /simulate y reproduce la línea de tiempo
      FreeCamera.cs              Cámara libre para recorrer la escena
    Assets/Scenes/Casilla        Escena principal, calibrada con seed=7 y 469 votantes
FORMULA_BETA.md                 Fórmula de la mezcla Beta y guía de verificación en Unity
```

## Requisitos

- Python 3.12 o posterior.
- Dependencias de Python incluidas en `backend/requirements.txt` (Flask,
  flasgger, Mesa, networkx, pytest).
- .NET 9 SDK, solo para compilar el cliente C# de referencia.
- Unity 6, para abrir el proyecto de visualización.

## Instalación

Desde la raíz del proyecto, crea un entorno virtual e instala las dependencias:

```powershell
cd backend
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

En macOS o Linux:

```bash
cd backend
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
```

## Ejecutar la demo de consola

```powershell
cd backend
.\.venv\Scripts\python.exe main.py
```

La demo escribe el flujo en la consola y termina cuando se procesan todos los
eventos. Para obtener una ejecución reproducible y más corta:

```powershell
.\.venv\Scripts\python.exe main.py --num-voters 30 --arrival-rate 0.5 --arrival-profile uniforme --seed 7
```

Opciones disponibles:

- `--num-voters`: número de llegadas a programar. Predeterminado: `200`.
- `--arrival-rate`: promedio de llegadas por minuto simulado, solo aplica al
  perfil `uniforme`. Predeterminado: `1/3`.
- `--arrival-profile`: `realista` (mezcla Beta, predeterminado) o `uniforme`
  (Poisson clásico).
- `--seed`: semilla entera para repetir una ejecución.
- `--secretario-capacity`, `--mesa-capacity`, `--casilla-capacity` y
  `--urna-capacity`: capacidad simultánea de cada estación. Predeterminado: `1`.
- `--rejection-rate`: probabilidad entre `0` y `1` de rechazar la INE después
  del secretario. Predeterminado: `0.02`.
- `--forced-event-kind`, `--forced-event-time`, `--forced-event-duration`:
  fuerzan el tipo, momento y duración del evento externo en vez de dejarlo a
  la semilla.

Ejemplo para explorar el efecto de un cuello de botella:

```powershell
.\.venv\Scripts\python.exe main.py --num-voters 1400 --casilla-capacity 3 --seed 7
```

Para reproducir la corrida calibrada contra la casilla real desde consola:

```powershell
.\.venv\Scripts\python.exe main.py --num-voters 469 --arrival-profile realista --secretario-capacity 2 --casilla-capacity 3 --seed 7
```

## API HTTP

El backend expone dos rutas: una para correr la simulación y otra para ver
sus resultados sin volver a calcularlos.

```powershell
cd backend
.\.venv\Scripts\python.exe server.py
```

### `POST /simulate`

Corre la simulación completa del lado del servidor y devuelve la línea de
tiempo entera en una sola respuesta, para que Unity la reproduzca sin volver
a consultar. Con el cuerpo vacío usa los valores por defecto del motor (no el
escenario calibrado a la casilla real, que vive solo en `GET /` y en el
inspector de Unity).

Petición: `POST http://127.0.0.1:5000/simulate` con `Content-Type:
application/json`. Todos los campos del cuerpo son opcionales.

| Campo | Tipo | Predeterminado | Descripción |
|---|---|---|---|
| `num_voters` | entero | `200` | Número de llegadas a programar. |
| `arrival_rate` | número > 0 | `0.3333…` (`1/3`) | **Promedio** de llegadas por minuto simulado si `arrival_profile` es `uniforme` o no se manda `arrival_beta`. |
| `arrival_profile` | `realista` o `uniforme` | `realista` | Perfil de llegada. `realista` usa la mezcla Beta de `FORMULA_BETA.md` salvo que se sobrescriba con `arrival_beta`. |
| `arrival_beta` | objeto o `null` | `null` | Mezcla Beta personalizada: `{"start_hour", "end_hour", "components": [{"weight", "alpha", "beta"}, ...]}`. Los pesos deben sumar 1. |
| `seed` | entero o `null` | `null` | Semilla para repetir una ejecución. |
| `secretario_capacity` | entero | `1` | Atenciones simultáneas en el secretario. |
| `mesa_capacity` | entero | `1` | Atenciones simultáneas en la mesa. |
| `casilla_capacity` | entero | `1` | Mamparas de votación simultáneas. |
| `urna_capacity` | entero | `1` | Depósitos simultáneos en la urna. |
| `rejection_rate` | número entre `0` y `1` | `0.02` | Probabilidad de rechazar la INE tras el secretario. |
| `forced_event_kind` | `corte_de_luz`, `temblor`, `aguacero` o `null` | `null` | Fuerza el tipo de evento externo. Con `null` lo decide la semilla. |
| `forced_event_time` | número > 0 o `null` | `null` | Minuto simulado en el que ocurre el evento. |
| `forced_event_duration` | número > 0 o `null` | `null` | Cuánto dura el evento, en minutos simulados. |

Ejemplo:

```bash
curl -X POST http://127.0.0.1:5000/simulate \
  -H "Content-Type: application/json" \
  -d '{"num_voters": 469, "arrival_profile": "realista", "seed": 7, "secretario_capacity": 2, "casilla_capacity": 3}'
```

Respuesta `200`: objeto con las claves `summary` (duración, conteos,
`results` con votos por candidato y ganador, capacidades por estación),
`movements`, `queue_events`, `station_events`, `voter_events` y
`external_events`.

Respuesta `400`: algún parámetro es inválido (por ejemplo `arrival_rate`
cero, negativo, no numérico, o una mezcla `arrival_beta` cuyos pesos no
suman 1). El cuerpo trae el motivo:

```json
{"error": "arrival_rate debe ser mayor que 0; se recibio 0."}
```

Los nombres son consistentes con el CLI: `--arrival-rate` ↔ `arrival_rate` ↔
el argumento `arrival_rate` de `CasillaModel`, con el mismo predeterminado
`1/3`.

### `GET /`

Sirve un dashboard HTML autocontenido (`backend/static/dashboard.html`) con
los resultados de una corrida:

- Si ya se llamó a `POST /simulate` (por ejemplo, porque Unity acaba de
  reproducir una jornada), muestra **esa misma corrida** — pensado para
  terminar en Unity y abrir el navegador a ver el resultado de la elección
  que se acaba de ver.
- Si no hay ninguna corrida previa, muestra el escenario calibrado contra la
  casilla real del PREP (`DEFAULT_SCENARIO`: 469 votantes, perfil
  `realista`, semilla `7`, capacidades `2/1/3/1`).
- Cualquier parámetro de la tabla anterior puede sobrescribirse por query
  string y fuerza una corrida nueva, por ejemplo:
  `http://127.0.0.1:5000/?seed=1&num_voters=300&arrival_profile=uniforme`.

## Ejecutar las pruebas

```powershell
cd backend
.\.venv\Scripts\python.exe -m pytest -v
```

La suite cubre el orden cronológico de eventos, tiempos no enteros, empates
de prioridad, límites de `run_until()`, reproducibilidad con semillas,
logging, llegadas (Poisson y mezcla Beta), traslados, capacidad y colas de
estaciones, pausa y reanudación, prioridad para adultos mayores, rechazo de
INE, coordinación del evento externo, los endpoints Flask (`/simulate` y
`/`) y la forma del JSON de línea de tiempo. En el estado actual del
proyecto, la suite local pasa sus **84 casos**, repartidos en
`test_casilla_model.py`, `test_beta_arrivals.py`, `test_server.py` y
`test_timeline.py`.

## Cliente C# y Unity

A diferencia de versiones anteriores de este documento, la integración
Unity ↔ Flask **está activa**: `SimulationRunner.cs` llama a `POST
/simulate`, recibe la línea de tiempo completa y la reproduce por completo
en la escena, sin volver a consultar al backend durante la reproducción.

### Cliente C#

```powershell
cd client-csharp
dotnet run
```

Cliente de referencia en línea de comandos que consulta el endpoint
configurado y muestra la respuesta JSON formateada; útil para inspeccionar
la API sin levantar Unity.

### Proyecto Unity

1. Levanta el backend y déjalo corriendo: `cd backend` y
   `.\.venv\Scripts\python.exe server.py`.
2. Abre `unity-client/MultiAgent-simulation` en Unity 6.
3. Abre `Assets/Scenes/Casilla`.
4. Selecciona el GameObject que contiene el componente `SimulationRunner` y
   revisa el inspector: por defecto reproduce el escenario calibrado contra
   la casilla real (469 votantes, perfil Realista, semilla 7, mezcla Beta
   activada, horario 08:00–18:00).
5. Presiona **Play**. La consola muestra `Timeline recibido` y (si la mezcla
   Beta está activa) `Formula Beta confirmada`, seguido de un conteo de
   llegadas por hora.
6. Al terminar la jornada aparece `JORNADA TERMINADA` junto con una gráfica
   de llegadas por hora en la esquina superior derecha.

Detalles de la fórmula Beta, los conteos esperados por hora con semilla 7 y
el procedimiento completo de verificación están en
[FORMULA_BETA.md](FORMULA_BETA.md).

## Ejemplo de salida (consola)

```text
[16:22:29] Votante 1 llega en t=0.54
[16:22:29] Votante 1 recibe TURN de secretario en t=0.54
[16:22:29] EVENTO EXTERNO: temblor en t=1.79 (dura 4.82 min)
[16:22:29] Coordinador envia PAUSE a secretario en t=1.79
[16:22:29] Votante 2 llega en t=2.12
[16:22:29] Votante 2 recibe WAIT de secretario en t=2.12
[16:22:29] Coordinador envia RESUME a secretario en t=6.61
[16:22:29] urna termina con votante 1 en t=11.80
[16:22:29] Votante 1 EXITS en t=11.80
[16:22:29] Simulación terminada en t=28.72 (6 votantes procesados, 0 rechazados)
```

## Estado y próximos pasos

### Implementado

- Motor de eventos y reloj simulado sobre el scheduler nativo de Mesa.
- Flujo completo de votantes, traslados y estaciones con capacidad configurable.
- Colas regular y prioritaria, mensajes entre agentes y pausa global por
  evento externo.
- Rechazo configurable de INE y registro estructurado en `event_log`.
- Perfil de llegada `realista` (mezcla Beta) calibrado contra la curva de
  afluencia de la casilla real, además del perfil `uniforme` (Poisson).
- Reparto de votos (`PAN` / `Movimiento Ciudadano` / `Morena`) calibrado
  contra el resultado real de la casilla PREP 2024 modelada.
- API Flask (`/simulate`, `/`) y dashboard HTML de resultados.
- Cliente Unity funcional que reproduce la línea de tiempo completa,
  incluyendo eventos externos y gráfica de llegadas por hora.
- Suite de pruebas automatizadas (84 casos) sobre el modelo, las llegadas,
  la API y el JSON de salida.

### En desarrollo / posibles extensiones

- Ampliar el catálogo de casillas de referencia más allá de la Sección
  3068 / Casilla 5, para comparar distintos perfiles de afluencia reales.
- Escenarios de escala adicionales (por ejemplo, casillas con lista nominal
  mucho mayor a 701).
- Más eventos externos y variantes de calibración por tipo de casilla
  (urbana/rural, básica/contigua).

## Verificación actual

La validación local de esta versión incluye:

- instalación de dependencias desde `backend/requirements.txt`;
- ejecución de la demo con reloj basado en eventos (`main.py`);
- ejecución de `python -m pytest -q`, con **84 pruebas exitosas**;
- ejecución de `server.py` y verificación manual de `POST /simulate` y
  `GET /` (dashboard con y sin corrida previa);
- verificación en Unity de que la escena `Casilla` reproduce la línea de
  tiempo, incluyendo la fórmula Beta y los conteos por hora documentados en
  [FORMULA_BETA.md](FORMULA_BETA.md).

## Uso de herramientas de IA

Se utilizaron herramientas de IA como apoyo para creación de código,
depuración, documentación y comprensión del stack tecnológico. La
implementación fue revisada y probada por el estudiante, quien comprende los
conceptos principales de Mesa, simulación de eventos discretos, comunicación
entre agentes, HTTP y la integración con Unity.
