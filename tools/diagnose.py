"""
Diagnose what changed in 2023-2025 to collapse the strategy.

Analyzes the Deals section across:
  - Trade frequency per month
  - Win rate per month
  - Average winner / average loser
  - Profit factor (gross profit / gross loss)
  - Average trade size (volume / lots)
  - Setup-type composition (FVG / OB / SZ / Liquidity Sweep)

Output: stdout table + CSV at reports/strategy_diagnosis.csv.
"""

from __future__ import annotations
import csv
import re
import warnings
from collections import defaultdict
from datetime import datetime
from pathlib import Path

import openpyxl

warnings.filterwarnings("ignore")

REPO = Path(__file__).resolve().parent.parent
XLSX = REPO / "ReportTester-12037216.xlsx"
DEALS_HEADER_ROW = 108684
OUT_CSV = REPO / "reports" / "strategy_diagnosis.csv"


def load_trades():
    """
    Pair 'in' deals with their following 'out' deal so we get one row per
    completed trade containing: time, volume, profit, entry-comment.
    """
    wb = openpyxl.load_workbook(XLSX, read_only=True, data_only=True)
    ws = wb["Sheet1"]

    trades = []
    pending_in = None  # the most recent 'in' deal awaiting its 'out'
    for row in ws.iter_rows(min_row=DEALS_HEADER_ROW + 1, values_only=True):
        time, _deal, _sym, _typ, direction, vol, _price, _ord, _comm, _swap, profit, _bal, comment = row[:13]
        if time is None or direction not in ("in", "out"):
            continue
        if isinstance(time, str):
            dt = datetime.strptime(time, "%Y.%m.%d %H:%M:%S")
        else:
            dt = time

        if direction == "in":
            pending_in = {
                "ts": dt,
                "volume": float(vol) if vol is not None else 0.0,
                "comment": comment or "",
            }
        else:  # out
            if pending_in is None:
                continue
            trades.append({
                "ts": pending_in["ts"],
                "volume": pending_in["volume"],
                "comment": pending_in["comment"],
                "profit": float(profit) if profit is not None else 0.0,
                "exit_comment": comment or "",
            })
            pending_in = None
    return trades


def setup_kind(comment):
    """Extract the SMC sub-strategy from the entry comment."""
    if not comment:
        return "Unknown"
    m = re.search(r"SMC-(\w+)", comment)
    return m.group(1) if m else "Unknown"


def monthly_stats(trades):
    """Bucket trades per month and compute headline metrics."""
    by_month = defaultdict(list)
    for t in trades:
        key = f"{t['ts'].year}-{t['ts'].month:02d}"
        by_month[key].append(t)

    rows = []
    for key in sorted(by_month):
        ts = by_month[key]
        n = len(ts)
        wins = [t["profit"] for t in ts if t["profit"] > 0]
        losses = [t["profit"] for t in ts if t["profit"] < 0]
        gp = sum(wins)
        gl = -sum(losses)
        wr = 100 * len(wins) / n if n else 0
        avg_w = gp / len(wins) if wins else 0
        avg_l = gl / len(losses) if losses else 0
        pf = gp / gl if gl else float("inf")
        avg_vol = sum(t["volume"] for t in ts) / n if n else 0
        net = gp - gl

        # Setup composition
        setup_counts = defaultdict(int)
        for t in ts:
            setup_counts[setup_kind(t["comment"])] += 1

        rows.append({
            "month": key,
            "trades": n,
            "win_rate_pct": wr,
            "avg_winner": avg_w,
            "avg_loser": avg_l,
            "profit_factor": pf,
            "avg_volume": avg_vol,
            "gross_profit": gp,
            "gross_loss": gl,
            "net": net,
            "fvg": setup_counts.get("FVG", 0),
            "ob": setup_counts.get("OB", 0),
            "sz": setup_counts.get("SZ", 0),
            "lsweep": setup_counts.get("LSweep", 0),
        })
    return rows


def yearly_rollup(monthly):
    """Aggregate monthly stats to year-level for the headline table."""
    yr = defaultdict(lambda: {
        "trades": 0, "wins": 0, "gp": 0.0, "gl": 0.0,
        "vol_sum": 0.0,
        "fvg": 0, "ob": 0, "sz": 0, "lsweep": 0,
    })
    # We need raw trade-level wins/gp/gl, recomputing from monthly is lossy.
    # Re-derive by parsing the monthly rows + reconstituting:
    for r in monthly:
        y = r["month"][:4]
        yr[y]["trades"] += r["trades"]
        # Approximate wins from win_rate * trades (close enough for yearly)
        yr[y]["wins"] += round(r["trades"] * r["win_rate_pct"] / 100)
        yr[y]["gp"] += r["gross_profit"]
        yr[y]["gl"] += r["gross_loss"]
        yr[y]["vol_sum"] += r["avg_volume"] * r["trades"]
        yr[y]["fvg"] += r["fvg"]
        yr[y]["ob"] += r["ob"]
        yr[y]["sz"] += r["sz"]
        yr[y]["lsweep"] += r["lsweep"]

    out = []
    for y in sorted(yr):
        d = yr[y]
        n = d["trades"]
        out.append({
            "year": y,
            "trades": n,
            "win_rate_pct": 100 * d["wins"] / n if n else 0,
            "profit_factor": d["gp"] / d["gl"] if d["gl"] else float("inf"),
            "avg_volume": d["vol_sum"] / n if n else 0,
            "gross_profit": d["gp"],
            "gross_loss": d["gl"],
            "net": d["gp"] - d["gl"],
            "fvg_pct": 100 * d["fvg"] / n if n else 0,
            "ob_pct": 100 * d["ob"] / n if n else 0,
            "sz_pct": 100 * d["sz"] / n if n else 0,
            "lsweep_pct": 100 * d["lsweep"] / n if n else 0,
        })
    return out


def print_yearly(yearly):
    print(f"\n{'Year':<6} {'Trades':>7} {'Win%':>6} {'PF':>5} {'AvgVol':>7} "
          f"{'Net $':>14} {'FVG%':>6} {'OB%':>6} {'SZ%':>6} {'LSw%':>6}")
    print("-" * 95)
    for r in yearly:
        pf = "inf" if r["profit_factor"] == float("inf") else f"{r['profit_factor']:.2f}"
        print(f"{r['year']:<6} {r['trades']:>7,} {r['win_rate_pct']:>5.1f}% {pf:>5} "
              f"{r['avg_volume']:>7.2f} {r['net']:>14,.2f} "
              f"{r['fvg_pct']:>5.1f}% {r['ob_pct']:>5.1f}% "
              f"{r['sz_pct']:>5.1f}% {r['lsweep_pct']:>5.1f}%")


def write_csv(monthly):
    OUT_CSV.parent.mkdir(exist_ok=True)
    with OUT_CSV.open("w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["month", "trades", "win_rate_pct", "avg_winner", "avg_loser",
                    "profit_factor", "avg_volume",
                    "gross_profit", "gross_loss", "net",
                    "fvg", "ob", "sz", "lsweep"])
        for r in monthly:
            pf = "inf" if r["profit_factor"] == float("inf") else f"{r['profit_factor']:.4f}"
            w.writerow([r["month"], r["trades"], f"{r['win_rate_pct']:.2f}",
                        f"{r['avg_winner']:.2f}", f"{r['avg_loser']:.2f}",
                        pf, f"{r['avg_volume']:.4f}",
                        f"{r['gross_profit']:.2f}", f"{r['gross_loss']:.2f}",
                        f"{r['net']:.2f}",
                        r["fvg"], r["ob"], r["sz"], r["lsweep"]])
    print(f"\nWrote {OUT_CSV}")


def main():
    print("Loading trades...")
    trades = load_trades()
    print(f"  {len(trades):,} round-trip trades\n")

    monthly = monthly_stats(trades)
    yearly = yearly_rollup(monthly)
    print_yearly(yearly)
    write_csv(monthly)


if __name__ == "__main__":
    main()
