"""Fixed-population arrival times sampled from a Beta mixture."""

import math


def _number(value, name):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError(f"{name} debe ser un numero finito.")
    return float(value)


def validate_beta_mixture(config):
    """Validate the two-component Beta formula and its same-day time window."""
    if not isinstance(config, dict):
        raise ValueError("arrival_beta debe ser un objeto.")
    start = _number(config.get("start_hour"), "arrival_beta.start_hour")
    end = _number(config.get("end_hour"), "arrival_beta.end_hour")
    if not 0 <= start < end <= 24:
        raise ValueError("El horario Beta debe cumplir 0 <= start_hour < end_hour <= 24.")

    components = config.get("components")
    if not isinstance(components, list) or not components:
        raise ValueError("arrival_beta.components debe contener al menos un componente.")
    result = []
    for component in components:
        if not isinstance(component, dict):
            raise ValueError("Cada componente Beta debe ser un objeto.")
        weight = _number(component.get("weight"), "arrival_beta.components.weight")
        alpha = _number(component.get("alpha"), "arrival_beta.components.alpha")
        beta = _number(component.get("beta"), "arrival_beta.components.beta")
        if weight < 0 or alpha <= 0 or beta <= 0:
            raise ValueError("Los pesos Beta deben ser >= 0 y alpha/beta deben ser > 0.")
        result.append({"weight": weight, "alpha": alpha, "beta": beta})
    total_weight = sum(component["weight"] for component in result)
    if not math.isclose(total_weight, 1.0, rel_tol=0, abs_tol=1e-9):
        raise ValueError("Los pesos de arrival_beta.components deben sumar 1.")
    return {"start_hour": start, "end_hour": end, "components": result}


def sample_beta_arrivals(rng, count, config):
    """Sample exactly ``count`` times from a mixture of Beta distributions."""
    components = config["components"]
    duration_minutes = (config["end_hour"] - config["start_hour"]) * 60
    times = []
    for component in rng.choices(
        components, weights=[component["weight"] for component in components], k=count
    ):
        normalized_time = rng.betavariate(component["alpha"], component["beta"])
        times.append(min(normalized_time * duration_minutes, math.nextafter(duration_minutes, 0)))
    return sorted(times)
