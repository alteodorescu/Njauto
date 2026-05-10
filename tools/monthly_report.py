"""
FundingPips account-churn simulator.

Reads the Deals section of an MT5 Strategy Tester report (Excel) and
walks every closed deal in chronological order, applying these rules
on each deal:

  - Starting balance per account: $100,000
  - Daily loss limit:   5% of day-start equity ($5,000) -> account lost
  - Max overall loss:   10% static from $100K ($10,000) -> account lost
  - Payout target:      4% above $100K ($4,000) -> payout taken,
                        balance reset to $100K, same account continues

A "loss" reset starts a NEW account; a "payout" reset keeps the same
account number. The output is a monthly table of accounts lost and
payouts taken plus running totals.
"""

from __future__ import annotations
import sys
import warnings
import csv
from collections import defaultdict
from datetime import datetime
from pathlib import Path

import openpyxl

warnings.filterwarnings("ignore")

XLSX = Path(__file__).resolve().parent.parent / "ReportTester-12037216.xlsx"
DEALS_HEADER_ROW = 108684  # row containing "Time, Deal, Symbol, ..."
START_BALANCE = 100_000.0
DAILY_LOSS_LIMIT = 5_000.0
OVERALL_LOSS_LIMIT = 10_000.0     # static from start
PAYOUT_TARGET = 4_000.0           # over $100K -> payout taken

# FundingPips 2 standard $100K challenge fee. Adjust if a promo / smaller
# account size is being modelled. Each new account purchased (initial +
# every loss replacement) pays this fee in the month it was bought.
CHALLENGE_FEE = 549.0


def load_deals():
    wb = openpyxl.load_workbook(XLSX, read_only=True, data_only=True)
    ws = wb["Sheet1"]

    deals = []
    for row in ws.iter_rows(min_row=DEALS_HEADER_ROW + 1, values_only=True):
        time, _deal, _sym, _typ, direction, _vol, _price, _ord, _comm, _swap, profit, balance, _comment = row[:13]
        if time is None or direction != "out" or profit is None:
            continue
        # Time is stored as text "YYYY.MM.DD HH:MM:SS"
        if isinstance(time, str):
            dt = datetime.strptime(time, "%Y.%m.%d %H:%M:%S")
        else:
            dt = time  # already a datetime
        deals.append((dt, float(profit)))

    return deals


def simulate(deals):
    """
    Returns events. events is a list of dicts with keys:
      ts, kind ('LOSS' | 'PAYOUT' | 'PURCHASE'), amount, account_no
    PURCHASE events emit CHALLENGE_FEE as a positive cost (recorded as
    negative in profit) every time a new account is bought - once at
    the very first deal, and once for each loss replacement.
    """
    events = []
    balance = START_BALANCE
    day_start_balance = START_BALANCE
    current_day = None
    account_no = 1

    if deals:
        # Initial challenge purchase, attributed to the first deal's month.
        events.append({
            "ts": deals[0][0],
            "kind": "PURCHASE",
            "amount": CHALLENGE_FEE,
            "account_no": account_no,
        })

    for ts, profit in deals:
        deal_day = ts.date()
        if current_day != deal_day:
            current_day = deal_day
            day_start_balance = balance

        balance += profit

        # Order of checks: overall floor (hardest), then daily, then payout.
        breached_overall = balance <= START_BALANCE - OVERALL_LOSS_LIMIT
        breached_daily = balance <= day_start_balance - DAILY_LOSS_LIMIT

        if breached_overall or breached_daily:
            events.append({
                "ts": ts,
                "kind": "LOSS",
                "amount": balance - START_BALANCE,  # negative
                "account_no": account_no,
                "breach": "overall" if breached_overall else "daily",
            })
            # Replacement account is purchased immediately.
            account_no += 1
            events.append({
                "ts": ts,
                "kind": "PURCHASE",
                "amount": CHALLENGE_FEE,
                "account_no": account_no,
            })
            balance = START_BALANCE
            day_start_balance = START_BALANCE
            continue

        if balance - START_BALANCE >= PAYOUT_TARGET:
            payout = balance - START_BALANCE
            events.append({
                "ts": ts,
                "kind": "PAYOUT",
                "amount": payout,
                "account_no": account_no,
            })
            balance = START_BALANCE
            day_start_balance = START_BALANCE

    return events


def monthly_summary(events):
    """Aggregate to (year, month) -> per-month stats including fees + profit."""
    by_month = defaultdict(lambda: {
        "losses_overall": 0,
        "losses_daily": 0,
        "payouts_count": 0,
        "payouts_sum": 0.0,
        "purchases_count": 0,
        "fees_sum": 0.0,
    })
    for e in events:
        key = (e["ts"].year, e["ts"].month)
        if e["kind"] == "LOSS":
            if e["breach"] == "overall":
                by_month[key]["losses_overall"] += 1
            else:
                by_month[key]["losses_daily"] += 1
        elif e["kind"] == "PAYOUT":
            by_month[key]["payouts_count"] += 1
            by_month[key]["payouts_sum"] += e["amount"]
        elif e["kind"] == "PURCHASE":
            by_month[key]["purchases_count"] += 1
            by_month[key]["fees_sum"] += e["amount"]
    return by_month


def write_report(events, by_month, deals):
    out_dir = Path(__file__).resolve().parent.parent / "reports"
    out_dir.mkdir(exist_ok=True)
    md = out_dir / "monthly_report.md"
    csv_out = out_dir / "monthly_report.csv"

    total_losses = sum(1 for e in events if e["kind"] == "LOSS")
    total_payouts_n = sum(1 for e in events if e["kind"] == "PAYOUT")
    total_payouts_sum = sum(e["amount"] for e in events if e["kind"] == "PAYOUT")
    total_purchases = sum(1 for e in events if e["kind"] == "PURCHASE")
    total_fees = sum(e["amount"] for e in events if e["kind"] == "PURCHASE")
    total_profit = total_payouts_sum - total_fees
    first_dt = deals[0][0] if deals else None
    last_dt = deals[-1][0] if deals else None

    lines = []
    lines.append("# FundingPips Account-Churn Monthly Report")
    lines.append("")
    lines.append(f"**Source**: `ReportTester-12037216.xlsx` (FundingPips2-SIM, NDX100, M5)  ")
    lines.append(f"**Trades simulated**: {len(deals):,} closed deals  ")
    lines.append(f"**Period**: {first_dt:%Y-%m-%d} → {last_dt:%Y-%m-%d}  ")
    lines.append("")
    lines.append("## Rules applied")
    lines.append("- Starting balance per account: **$100,000**")
    lines.append(f"- Daily loss limit: **${DAILY_LOSS_LIMIT:,.0f}** from day-start equity (5%) -> account lost")
    lines.append(f"- Max overall loss: **${OVERALL_LOSS_LIMIT:,.0f}** static from start (10%) -> account lost")
    lines.append(f"- Payout target: **${PAYOUT_TARGET:,.0f}** above $100K (4%) -> payout taken, balance reset to $100K, account continues")
    lines.append(f"- Challenge fee: **${CHALLENGE_FEE:,.0f}** per new account (initial purchase + every loss replacement)")
    lines.append("- Lost accounts are replaced with a fresh $100K account; account counter increments.")
    lines.append("")
    lines.append("## Totals")
    lines.append(f"- **Accounts purchased**: {total_purchases:,} (1 initial + {total_losses:,} replacements)")
    lines.append(f"- **Accounts lost**: {total_losses:,}")
    lines.append(f"- **Payouts taken**: {total_payouts_n:,}")
    lines.append(f"- **Total payout $**: ${total_payouts_sum:,.2f}")
    lines.append(f"- **Total fees $**: ${total_fees:,.2f}")
    lines.append(f"- **Net profit $**: ${total_profit:,.2f}")
    lines.append(f"- **Avg payout**: ${(total_payouts_sum/total_payouts_n if total_payouts_n else 0):,.2f}")
    lines.append("")
    lines.append("## Monthly breakdown")
    lines.append("")
    lines.append("| Month | Lost (overall) | Lost (daily) | Lost total | Payouts # | Payouts $ | Challenges # | Fees $ | Profit $ |")
    lines.append("|---|---:|---:|---:|---:|---:|---:|---:|---:|")

    for key in sorted(by_month.keys()):
        y, m = key
        d = by_month[key]
        lost_total = d["losses_overall"] + d["losses_daily"]
        profit = d["payouts_sum"] - d["fees_sum"]
        lines.append(
            f"| {y}-{m:02d} | {d['losses_overall']} | {d['losses_daily']} | "
            f"{lost_total} | {d['payouts_count']} | ${d['payouts_sum']:,.2f} | "
            f"{d['purchases_count']} | ${d['fees_sum']:,.2f} | ${profit:,.2f} |"
        )

    md.write_text("\n".join(lines) + "\n")
    print(f"Wrote {md}")

    # Also CSV for easy spreadsheet import
    with csv_out.open("w", newline="") as f:
        w = csv.writer(f)
        w.writerow([
            "month", "lost_overall", "lost_daily", "lost_total",
            "payouts_count", "payouts_sum_usd",
            "challenges_count", "fees_sum_usd", "profit_usd",
        ])
        for key in sorted(by_month.keys()):
            y, m = key
            d = by_month[key]
            lost_total = d["losses_overall"] + d["losses_daily"]
            profit = d["payouts_sum"] - d["fees_sum"]
            w.writerow([
                f"{y}-{m:02d}", d["losses_overall"], d["losses_daily"], lost_total,
                d["payouts_count"], f"{d['payouts_sum']:.2f}",
                d["purchases_count"], f"{d['fees_sum']:.2f}", f"{profit:.2f}",
            ])
    print(f"Wrote {csv_out}")
    return md, csv_out, total_losses, total_payouts_n, total_payouts_sum, total_fees, total_profit


def main():
    print("Loading deals...")
    deals = load_deals()
    print(f"  {len(deals):,} closed deals")
    print("Simulating...")
    events = simulate(deals)
    print(f"  {len(events):,} events")
    by_month = monthly_summary(events)
    md, csv_out, losses, payouts, payouts_sum, fees, profit = write_report(events, by_month, deals)
    print()
    print(f"TOTALS: lost={losses}  payouts={payouts}  payouts_sum=${payouts_sum:,.2f}  "
          f"fees=${fees:,.2f}  profit=${profit:,.2f}")


if __name__ == "__main__":
    main()
