# Fórmula Beta de llegadas

La escena `Assets/Scenes/Casilla` usa por defecto:

`0.82 Beta((t-8)/10; 2,4) + 0.18 Beta((t-8)/10; 6,2)`

Se conservan exactamente 1400 personas. Para cada una, Python elige el primer
componente con probabilidad 0.82 o el segundo con probabilidad 0.18, genera una
muestra Beta entre 0 y 1 y la convierte al intervalo de 08:00 a 18:00. Después
ordena los tiempos. La semilla permite repetir exactamente la misma corrida.

## Verificar en Unity

1. Abre PowerShell y entra a la carpeta `backend` de esta copia.
2. Si todavía no existe `.venv`, ejecuta:

   ```powershell
   python -m venv .venv
   .\.venv\Scripts\python.exe -m pip install -r requirements.txt
   ```

3. Ejecuta las pruebas:

   ```powershell
   .\.venv\Scripts\python.exe -m pytest -q
   ```

   El resultado esperado en esta versión es `57 passed`.

4. Inicia el backend y deja abierta esa ventana:

   ```powershell
   .\.venv\Scripts\python.exe server.py
   ```

5. Abre `unity-client/MultiAgent-simulation` con Unity.
6. Abre `Assets/Scenes/Casilla`.
7. Selecciona el GameObject que contiene el componente `SimulationRunner`.
8. En el Inspector comprueba:
   - `Num Voters`: 1400.
   - `Usar Formula Beta`: activado.
   - `Hora Fin Beta`: 18.
   - `Hora Inicio`: 8.
   - `Seed`: 7.
9. Abre `Window > General > Console`, limpia mensajes anteriores y pulsa Play.
10. Comprueba que aparezcan `Timeline recibido` y `Formula Beta confirmada`.
    A continuación Unity imprime un conteo por cada hora de 08:00 a 18:00.
11. Con semilla 7 y 1400 personas, usando las capacidades guardadas en la escena,
    los conteos esperados son:

    | Hora | Llegadas |
    |---|---:|
    | 08–09 | 117 |
    | 09–10 | 202 |
    | 10–11 | 227 |
    | 11–12 | 191 |
    | 12–13 | 207 |
    | 13–14 | 156 |
    | 14–15 | 108 |
    | 15–16 | 83 |
    | 16–17 | 75 |
    | 17–18 | 34 |

    Deben sumar 1400. Cambiar la semilla cambia los conteos; mantenerla en 7
    reproduce los mismos valores.

12. Verifica visualmente que no aparezcan votantes nuevos después de las 18:00.
    Las personas que ya estaban dentro pueden terminar después de esa hora. Con
    esta semilla el backend termina aproximadamente a las 18:03:24.

## Gráfica en Unity

La gráfica aparece en la esquina superior derecha del Game View únicamente
cuando se muestra `JORNADA TERMINADA`. Presenta una barra por hora para toda la
corrida, además del modo de llegada, semilla, población, capacidades y evento externo.
Puede ocultarse desactivando `Mostrar Grafica Llegadas` en el componente
`SimulationRunner`. La gráfica es informativa y no modifica la simulación.

`Speed = 60` reproduce un minuto simulado por segundo real: diez horas duran
aproximadamente diez minutos. Para revisar más rápido usa temporalmente 600;
la jornada se reproduce en cerca de un minuto y los resultados no cambian.

Si Unity indica que el servidor no reconoce la fórmula, hay otro `server.py`
ocupando el puerto 5000. Detén ese proceso e inicia el backend de esta carpeta.
Al desactivar `Usar Formula Beta`, Unity utiliza el modo Poisson original basado
en `Promedio Llegadas Por Minuto`.
