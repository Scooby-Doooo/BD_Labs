"""Зводить сирі заміри в таблиці (медіана по раундах) і будує графіки звіту."""
import csv
import glob
import os
from collections import defaultdict
from statistics import median

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.ticker import FuncFormatter, LogLocator, NullLocator

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(ROOT, "measurements")
IMAGES = os.path.join(ROOT, "docs", "images")
PG, MONGO = "PostgreSQL", "MongoDB"
COLORS = {PG: "#2a78d6", MONGO: "#eb6834"}
INK, MUTED, GRID = "#0b0b0b", "#52514e", "#e4e3df"
SCENARIOS = [
    ("card_uniform", "Картка проєкту, випадковий id"),
    ("card_hot", "Картка проєкту, гарячі id"),
    ("catalog", "Каталог: навички + бюджет"),
    ("by_attribute", "Фільтр за характеристикою"),
    ("search", "Повнотекстовий пошук"),
    ("freelancer_proposals", "Заявки фрілансера"),
    ("stats_by_category", "Аналітика за категоріями"),
    ("top_freelancers", "Топ фрілансерів"),
    ("create_project", "Новий проєкт"),
    ("add_proposal", "Нова заявка"),
    ("accept_proposal", "Прийняття заявки"),
    ("rename_freelancer", "Перейменування фрілансера"),
    ("rename_skill", "Перейменування навички"),
    ("mixed_80_15_5", "Змішане навантаження"),
]
LABEL = dict(SCENARIOS)


def load():
    # (проєктів, база, сценарій, потоки) -> список рядків по раундах
    rows = defaultdict(list)
    for path in glob.glob(os.path.join(DATA, "raw_*.csv")):
        with open(path, encoding="utf-8") as f:
            for r in csv.DictReader(f):
                rows[int(r["projects"]), r["db"], r["scenario"], int(r["threads"])].append(r)
    return rows


def med(rows, key, field):
    values = [float(r[field]) for r in rows.get(key, [])]
    return median(values) if values else None


def num(v, digits=2):
    if v is None:
        return "—"
    if digits == 0 and v < 10:
        digits = 1
    return f"{v:,.{digits}f}".replace(",", " ").replace(".", ",")


def ratio(a, b):
    if not a or not b:
        return "—"
    return f"PG ×{num(b / a, 1)}" if a < b else f"Mongo ×{num(a / b, 1)}"


def tables(rows):
    volumes = sorted({k[0] for k in rows})
    out = []
    for n in volumes:
        out.append(f"\n### {n:,} проєктів\n".replace(",", " "))
        out.append("| Сценарій | Потоки | p50 PG, мс | p50 Mongo, мс | p95 PG, мс | p95 Mongo, мс | оп/с PG | оп/с Mongo | Швидше |")
        out.append("|---|---:|---:|---:|---:|---:|---:|---:|---|")
        for scenario, label in SCENARIOS:
            for t in (1, 16):
                pk, mk = (n, PG, scenario, t), (n, MONGO, scenario, t)
                if pk not in rows:
                    continue
                p50p, p50m = med(rows, pk, "p50_ms"), med(rows, mk, "p50_ms")
                opsp, opsm = med(rows, pk, "ops_s"), med(rows, mk, "ops_s")
                # На 16 потоках система насичена, тому порівнюємо пропускну здатність.
                faster = ratio(1 / opsp, 1 / opsm) if t == 16 else ratio(p50p, p50m)
                out.append(f"| {label} | {t} | {num(p50p)} | {num(p50m)} | {num(med(rows, pk, 'p95_ms'))} | {num(med(rows, mk, 'p95_ms'))} | {num(opsp, 0)} | {num(opsm, 0)} | {faster} |")
        rp, rm = (n, "PostgreSQL relaxed", "add_proposal", 16), (n, "MongoDB relaxed", "add_proposal", 16)
        if rp in rows:
            out.append(f"| Нова заявка без очікування диска | 16 | {num(med(rows, rp, 'p50_ms'))} | {num(med(rows, rm, 'p50_ms'))} | {num(med(rows, rp, 'p95_ms'))} | {num(med(rows, rm, 'p95_ms'))} | {num(med(rows, rp, 'ops_s'), 0)} | {num(med(rows, rm, 'ops_s'), 0)} | {ratio(1 / med(rows, rp, 'ops_s'), 1 / med(rows, rm, 'ops_s'))} |")
    return "\n".join(out) + "\n"


def plain(value, _):
    return f"{value:,.0f}".replace(",", " ") if value >= 1 else f"{value:g}".replace(".", ",")


def style(ax):
    for side in ("top", "right", "left"):
        ax.spines[side].set_visible(False)
    ax.spines["bottom"].set_color(GRID)
    ax.tick_params(colors=MUTED, length=0)


def dot_plot(rows, n, path):
    items = [(s, l) for s, l in SCENARIOS if s != "mixed_80_15_5" and (n, PG, s, 1) in rows][::-1]
    fig, ax = plt.subplots(figsize=(9.5, 6.2), dpi=150)
    for y, (s, _) in enumerate(items):
        p, m = med(rows, (n, PG, s, 1), "p50_ms"), med(rows, (n, MONGO, s, 1), "p50_ms")
        ax.plot([p, m], [y, y], color=GRID, lw=2, zorder=1)
        ax.scatter(p, y, s=70, color=COLORS[PG], edgecolor="white", linewidth=1.5, zorder=2, label=PG if y == 0 else None)
        ax.scatter(m, y, s=70, color=COLORS[MONGO], edgecolor="white", linewidth=1.5, zorder=2, label=MONGO if y == 0 else None)
        factor = max(p, m) / min(p, m)
        ratio_text = f"×{factor:.0f}" if factor >= 100 else f"×{factor:.1f}".replace(".", ",")
        ax.annotate(ratio_text, (max(p, m), y), xytext=(9, 0), textcoords="offset points", va="center", fontsize=9, color=MUTED)
    ax.set_xscale("log")
    ax.set_yticks(range(len(items)), [l for _, l in items], color=INK, fontsize=10)
    ax.set_xlabel("Медіанна затримка (p50), мс, логарифмічна шкала", color=MUTED, fontsize=10)
    ax.grid(axis="x", color=GRID, lw=0.8)
    ax.set_axisbelow(True)
    ax.set_xlim(right=ax.get_xlim()[1] * 4)
    ax.xaxis.set_major_formatter(FuncFormatter(plain))
    style(ax)
    ax.legend(frameon=False, loc="upper right", fontsize=10)
    fig.suptitle("Затримка одного клієнта, " + f"{n:,}".replace(",", " ") + " проєктів", x=0.01, ha="left", color=INK, fontsize=13, fontweight="bold")
    fig.tight_layout()
    fig.savefig(path, facecolor="white")
    plt.close(fig)


def growth(rows, path):
    picks = ["card_uniform", "catalog", "freelancer_proposals", "stats_by_category", "add_proposal", "rename_skill"]
    volumes = sorted({k[0] for k in rows})
    fig, axes = plt.subplots(2, 3, figsize=(10.5, 6), dpi=150)
    for ax, s in zip(axes.flat, picks):
        for db in (PG, MONGO):
            ys = [med(rows, (n, db, s, 1), "p50_ms") for n in volumes]
            ax.plot(volumes, ys, color=COLORS[db], lw=2, marker="o", ms=6, mec="white", label=db)
        ax.set_xscale("log")
        ax.set_yscale("log")
        ax.set_xticks(volumes, [f"{v // 1000}k" for v in volumes])
        ax.xaxis.set_minor_locator(NullLocator())
        ax.yaxis.set_major_locator(LogLocator(subs=(1, 2, 5), numticks=12))
        ax.yaxis.set_minor_locator(NullLocator())
        ax.yaxis.set_major_formatter(FuncFormatter(plain))
        ax.set_title(LABEL[s], loc="left", fontsize=10.5, color=INK)
        ax.grid(axis="y", color=GRID, lw=0.8)
        style(ax)
    axes.flat[0].legend(frameon=False, fontsize=9)
    fig.supylabel("p50, мс, логарифмічна шкала", color=MUTED, fontsize=10)
    fig.supxlabel("Кількість проєктів", color=MUTED, fontsize=10)
    fig.suptitle("Як затримка зростає разом з обсягом даних", x=0.01, ha="left", color=INK, fontsize=13, fontweight="bold")
    fig.tight_layout()
    fig.savefig(path, facecolor="white")
    plt.close(fig)


def main():
    rows = load()
    with open(os.path.join(DATA, "summary.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(tables(rows))
    os.makedirs(IMAGES, exist_ok=True)
    biggest = max(k[0] for k in rows)
    dot_plot(rows, biggest, os.path.join(IMAGES, "latency_single_client.png"))
    growth(rows, os.path.join(IMAGES, "latency_growth.png"))


if __name__ == "__main__":
    main()
