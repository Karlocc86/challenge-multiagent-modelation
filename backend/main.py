"""Console demo of the casilla INE event-driven simulation core.

Schedules voter arrivals on Mesa's built-in priority queue and runs each
voter through the secretario -> mesa -> casilla -> urna station chain,
plus one external event that pauses every station for a while. The clock
is driven to completion event by event (never a ``step()`` loop), so the
printed timestamps show genuine event-driven time jumps instead of
fixed-tick advancement.
"""

import argparse
import logging

from casilla import CasillaModel


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Demo del motor de eventos + reloj simulado (casilla INE)."
    )
    parser.add_argument("--num-voters", type=int, default=200)
    parser.add_argument("--arrival-rate", type=float, default=1 / 3)
    parser.add_argument("--seed", type=int, default=None)
    parser.add_argument("--secretario-capacity", type=int, default=1)
    parser.add_argument("--mesa-capacity", type=int, default=1)
    parser.add_argument("--casilla-capacity", type=int, default=1)
    parser.add_argument("--urna-capacity", type=int, default=1)
    parser.add_argument("--rejection-rate", type=float, default=0.02)
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.INFO, format="[%(asctime)s] %(message)s", datefmt="%H:%M:%S"
    )

    model = CasillaModel(
        num_voters=args.num_voters,
        arrival_rate=args.arrival_rate,
        secretario_capacity=args.secretario_capacity,
        mesa_capacity=args.mesa_capacity,
        casilla_capacity=args.casilla_capacity,
        urna_capacity=args.urna_capacity,
        rejection_rate=args.rejection_rate,
        rng=args.seed,
    )

    model.run_to_completion()

    num_exits = sum(1 for entry in model.event_log if entry["event"] == "EXIT")
    num_rejected = sum(1 for entry in model.event_log if entry["event"] == "REJECTED")
    logging.info(
        "Simulación terminada en t=%.2f (%d votantes procesados, %d rechazados)",
        model.time,
        num_exits,
        num_rejected,
    )


if __name__ == "__main__":
    main()
