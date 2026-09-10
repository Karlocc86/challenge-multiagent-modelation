# Simulación multiagente de una casilla INE

Simulación de eventos discretos para estudiar el flujo de votantes dentro de una casilla electoral. El backend está construido con Python y Mesa: genera llegadas siguiendo un proceso de Poisson, modela estaciones con capacidad limitada y colas de espera, y avanza un reloj simulado saltando directamente entre eventos.

> **Estado actual:** el núcleo de simulación funciona como una demo de consola y cuenta con pruebas automatizadas. La API Flask, el cliente C# y la visualización de Unity permanecen en pausa mientras se define su nueva integración con el modelo actual.

## Objetivos

- Representar el recorrido `entrada -> secretario -> mesa -> casilla -> urna -> salida`.
- Modelar tiempos de servicio, traslados y llegadas en minutos simulados.
- Aplicar capacidad configurable por estación y colas de espera.
- Representar comunicación entre agentes mediante mensajes explícitos.
- Registrar eventos para facilitar pruebas, análisis y una futura visualización.
- Mantener una base extensible para una API y una escena interactiva.

## Arquitectura

```text
                    +-----------------------------+
                    |  Mesa: reloj y event queue  |
                    +--------------+--------------+
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
                         logs de consola / pytest

  Integraciones previstas: API Flask -> cliente C# / Unity
```

## Flujo de la simulación

```text
Llegada de votantes (Poisson, --arrival-rate)
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
        | (duración aleatoria)
        v
  Coordinador --RESUME--> secretario, mesa, casilla, urna
```

- `CasillaModel` usa el scheduler de eventos nativo de Mesa y no un ciclo de ticks fijos. `run_to_completion()` procesa la cola hasta que no quedan eventos pendientes.
- Cada estación (`Station`) tiene capacidad configurable, tiempo de servicio aleatorio y dos colas: una prioritaria para adultos mayores y otra regular. Cada cola conserva el orden de llegada.
- Los votantes (`VoterAgent`) reaccionan a mensajes `TURN`, `WAIT` y `REJECTED`. Las estaciones y el coordinador también se comunican mediante objetos `Message`.
- Después del secretario, la INE puede rechazarse según `--rejection-rate`. El votante rechazado no continúa hacia las estaciones restantes.
- En cada ejecución se programa un evento externo. El coordinador pausa las cuatro estaciones y las reanuda al terminar la duración del evento.
- `event_log` conserva llegadas, movimientos, filas, terminaciones, rechazos, salidas y el evento externo. Los detalles de comunicación se emiten mediante `logging`.

Todas las duraciones están expresadas en **minutos simulados**. Los movimientos se registran como segmentos con tiempo inicial y final, dejando la interpolación visual para la futura integración con Unity.

## Estructura del proyecto

```text
backend/
  casilla/
    __init__.py               Exporta CasillaModel
    model.py                  Modelo, scheduler y coordinación del flujo
    agents.py                 VoterAgent, Station, Coordinador y Message
  tests/
    conftest.py               Configuración de importación para pytest
    test_casilla_model.py     Suite del scheduler, flujo y agentes
  main.py                     Punto de entrada de la demo de consola
  requirements.txt            Dependencias de Python
client-csharp/
  Program.cs                  Cliente HTTP C# (en pausa)
unity-client/
  MultiAgent-simulation/      Proyecto Unity
    Assets/Scripts/
      FlaskAgentClient.cs     Integración con la API (en pausa)
```

## Requisitos

- Python 3.12 o posterior.
- Dependencias de Python incluidas en `backend/requirements.txt`.
- .NET 9 SDK, solo para compilar el cliente C# en pausa.
- Unity 6, solo para abrir el proyecto de visualización en pausa.

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

## Ejecutar la demo

```powershell
cd backend
.\.venv\Scripts\python.exe main.py
```

La demo escribe el flujo en la consola y termina cuando se procesan todos los eventos. Para obtener una ejecución reproducible y más corta:

```powershell
.\.venv\Scripts\python.exe main.py --num-voters 30 --arrival-rate 0.5 --seed 7
```

Opciones disponibles:

- `--num-voters`: número de llegadas a programar. Predeterminado: `200`.
- `--arrival-rate`: promedio de llegadas por minuto simulado. Predeterminado: `1/3`.
- `--seed`: semilla entera para repetir una ejecución.
- `--secretario-capacity`, `--mesa-capacity`, `--casilla-capacity` y `--urna-capacity`: capacidad simultánea de cada estación. Predeterminado: `1`.
- `--rejection-rate`: probabilidad entre `0` y `1` de rechazar la INE después del secretario. Predeterminado: `0.02`.

Ejemplo para explorar el efecto de un cuello de botella:

```powershell
.\.venv\Scripts\python.exe main.py --num-voters 1400 --casilla-capacity 3 --seed 7
```

## API HTTP (POST /simulate)

El backend expone un único endpoint. Corre la simulación completa del lado del
servidor y devuelve la línea de tiempo entera en una sola respuesta, para que
Unity la reproduzca sin volver a consultar.

```powershell
cd backend
.\.venv\Scripts\python.exe server.py
```

Petición: `POST http://127.0.0.1:5000/simulate` con `Content-Type: application/json`.
Todos los campos del cuerpo son opcionales; sin cuerpo se usan los predeterminados.

| Campo | Tipo | Predeterminado | Descripción |
|---|---|---|---|
| `num_voters` | entero | `200` | Número de llegadas a programar. |
| `arrival_rate` | número > 0 | `0.3333…` (`1/3`) | **Promedio** de llegadas por minuto simulado. Los huecos entre llegadas siguen siendo aleatorios alrededor de este valor. |
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
  -d '{"num_voters": 30, "arrival_rate": 0.5, "seed": 7}'
```

Respuesta `200`: objeto con las claves `summary`, `movements`, `queue_events`,
`station_events`, `voter_events` y `external_events`.

Respuesta `400`: parámetro inválido — `arrival_rate` o los `forced_event_*` en
cero, negativos, no numéricos o no finitos, o un tipo de evento desconocido. El
cuerpo trae el motivo:

```json
{"error": "arrival_rate debe ser mayor que 0; se recibio 0."}
```

Los nombres son consistentes con el CLI: `--arrival-rate` ↔ `arrival_rate` ↔ el
argumento `arrival_rate` de `CasillaModel`, con el mismo predeterminado `1/3`.

## Ejecutar las pruebas

```powershell
cd backend
.\.venv\Scripts\python.exe -m pytest -v
```

La suite cubre el orden cronológico de eventos, tiempos no enteros, empates de prioridad, límites de `run_until()`, reproducibilidad con semillas, logging, llegadas, traslados, capacidad y colas de estaciones, pausa y reanudar, prioridad para adultos mayores, rechazo de INE y coordinación del evento externo. En el estado actual del proyecto, la suite local pasa sus 24 casos.

## Cliente C# y Unity

Ambas integraciones dependen de un endpoint Flask (`/get_agents`) que no está expuesto por el backend actual. Se conservan en el repositorio como base para un incremento posterior, pero no forman parte de la ejecución funcional de esta versión.

### Cliente C#

```powershell
cd client-csharp
dotnet run
```

El cliente intenta consultar el endpoint configurado y muestra la respuesta JSON formateada. Actualmente terminará con un error de conexión mientras la API no sea reincorporada.

### Proyecto Unity

1. Abre `unity-client/MultiAgent-simulation` en Unity 6.
2. Abre `Assets/Scenes/SampleScene`.
3. Presiona **Play**.

La escena intentará consultar Flask cada segundo y mostrará un error de conexión hasta que exista un contrato de API compatible con el modelo actual.

## Ejemplo de salida

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
- Colas regular y prioritaria, mensajes entre agentes y pausa global.
- Rechazo configurable de INE y registro estructurado en `event_log`.
- Pruebas automatizadas del comportamiento principal.

### En desarrollo

- API Flask para controlar la simulación y consultar su estado.
- Contrato de datos entre el backend, el cliente C# y Unity.
- Visualización de posiciones y movimientos a partir de los segmentos de tiempo registrados.
- Escenarios de escala y calibración de parámetros para aproximadamente 1400 votantes.

## Verificación actual

La validación local de esta versión incluye:

- instalación de dependencias desde `backend/requirements.txt`;
- ejecución de la demo con reloj basado en eventos;
- ejecución de `python -m pytest -q`, con 24 pruebas exitosas;
- revisión de que C# y Unity permanecen identificados como integraciones pendientes, no como funcionalidades activas.

## Uso de herramientas de IA

Se utilizaron herramientas de IA como apoyo para creación de código, depuración, documentación y comprensión del stack tecnológico. La implementación fue revisada y probada por el estudiante, quien comprende los conceptos principales de Mesa, simulación de eventos discretos, comunicación entre agentes, HTTP y la integración prevista con Unity.
