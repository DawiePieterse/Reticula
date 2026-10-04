"""Design report PDF (plan 6.3): design basis, loads, LV, MV and bulk supply results, assumptions and traceability.

Every number is taken from the stored results with its clause reference; traced values list their formula and
inputs. The footer of every page carries the stamp (plan 6.8).
"""

from __future__ import annotations

import io
import os
from pathlib import Path
from typing import Any

from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import KeepTogether, PageBreak, Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle

from .package import DocumentPackage, chosen_option, load_template, stamp_text, unverified

FONT_DIRS = ("/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/dejavu", "/usr/share/fonts/TTF")


def _register_fonts() -> tuple[str, str]:
    """DejaVu Sans (Unicode: Δ, Σ, β, ≤ in formulas) when installed, else Helvetica."""
    for d in [os.environ.get("RETICULA_FONT_DIR", ""), *FONT_DIRS]:
        regular, bold = Path(d) / "DejaVuSans.ttf", Path(d) / "DejaVuSans-Bold.ttf"
        if d and regular.is_file() and bold.is_file():
            pdfmetrics.registerFont(TTFont("DejaVuSans", str(regular)))
            pdfmetrics.registerFont(TTFont("DejaVuSans-Bold", str(bold)))
            pdfmetrics.registerFontFamily("DejaVuSans", normal="DejaVuSans", bold="DejaVuSans-Bold", italic="DejaVuSans", boldItalic="DejaVuSans-Bold")
            return "DejaVuSans", "DejaVuSans-Bold"
    return "Helvetica", "Helvetica-Bold"


FONT, FONT_BOLD = _register_fonts()
_STYLES = getSampleStyleSheet()
BODY = ParagraphStyle("body", parent=_STYLES["BodyText"], fontName=FONT, fontSize=9, leading=11.5)
SMALL = ParagraphStyle("small", parent=BODY, fontSize=7.5, leading=9)
H1 = ParagraphStyle("h1", parent=_STYLES["Heading1"], fontName=FONT_BOLD, fontSize=15, spaceAfter=6)
H2 = ParagraphStyle("h2", parent=_STYLES["Heading2"], fontName=FONT_BOLD, fontSize=12, spaceBefore=8, spaceAfter=4)
H3 = ParagraphStyle("h3", parent=_STYLES["Heading3"], fontName=FONT_BOLD, fontSize=10, spaceBefore=6, spaceAfter=3)
WARN = ParagraphStyle("warn", parent=BODY, textColor=colors.HexColor("#9a3412"), backColor=colors.HexColor("#fff7ed"), borderPadding=4)


def _p(text: Any, style: ParagraphStyle = SMALL) -> Paragraph:
    return Paragraph(str(text).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"), style)


def table(rows: list[list[Any]], widths: list[float] | None = None, header: bool = True) -> Table:
    data = [[c if isinstance(c, Paragraph) else _p(c) for c in r] for r in rows]
    t = Table(data, colWidths=widths, repeatRows=1 if header else 0)
    style = [("GRID", (0, 0), (-1, -1), 0.25, colors.HexColor("#c8ccd0")), ("VALIGN", (0, 0), (-1, -1), "TOP"),
             ("LEFTPADDING", (0, 0), (-1, -1), 3), ("RIGHTPADDING", (0, 0), (-1, -1), 3)]
    if header:
        style.append(("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#eef1f4")))
    t.setStyle(TableStyle(style))
    return t


def _fmt(v: Any, nd: int = 2) -> str:
    if isinstance(v, float):
        return f"{v:,.{nd}f}"
    return "—" if v is None else str(v)


def traced_rows(name: str, t: dict) -> list[Any]:
    inputs = "; ".join(f"{i['name']} = {i['value']} {i.get('unit', '')}".strip() for i in t.get("inputs", []))
    return [name, f"{_fmt(t['value'])} {t['unit']}", t["formula_id"], t["formula"], t.get("clause", ""), inputs]


def check_rows(checks: list[dict], where: str = "") -> list[list[Any]]:
    return [[where, c["code"], c["subject"], f"{_fmt(c['value'])} {c['unit']}", f"{_fmt(c['limit'])} {c['unit']}", "Pass" if c["passed"] else "FAIL",
             c.get("clause", "")] for c in checks]


def render_report(pkg: DocumentPackage, sheets: list[Any] | None = None) -> bytes:
    tpl = load_template(pkg.project.authority)
    buf = io.BytesIO()
    footer = stamp_text(pkg)

    def on_page(canvas, doc):
        canvas.saveState()
        canvas.setFont(FONT, 6.5)
        half = footer.find(" · Design date")
        canvas.drawString(15 * mm, 9.5 * mm, footer[:half] if half > 0 else footer)
        canvas.drawString(15 * mm, 6.5 * mm, footer[half + 3:] if half > 0 else "")
        canvas.drawRightString(A4[0] - 15 * mm, 8 * mm, f"Page {doc.page}")
        canvas.drawString(15 * mm, A4[1] - 9 * mm, f"{pkg.project.name} – design report – revision {pkg.stamp.revision}")
        canvas.restoreState()

    doc = SimpleDocTemplate(buf, pagesize=A4, leftMargin=15 * mm, rightMargin=15 * mm, topMargin=15 * mm, bottomMargin=15 * mm,
                            title=f"{pkg.project.name} design report", author="Reticula", subject=footer)
    W = A4[0] - 30 * mm
    s: list[Any] = []
    s += [Paragraph(f"{pkg.project.name}", H1), Paragraph("Electrification design report", H2), _p(tpl.get("title", ""), BODY), Spacer(1, 6)]
    s.append(table([["Stamp", ""], ["Design rules", f"{pkg.stamp.rules} (hash {pkg.stamp.rules_hash})"], ["Rate list", f"{pkg.stamp.rate_list}, rate date {pkg.stamp.rate_date}"],
                    ["Design date", pkg.stamp.design_date], ["Revision", pkg.stamp.revision], ["Engineer", pkg.stamp.engineer or "—"],
                    ["Signed off", pkg.stamp.signed_off or "Not signed off (draft)"],
                    ["Generated", pkg.stamp.generated_at]], [45 * mm, W - 45 * mm]))
    if unverified(pkg):
        s += [Spacer(1, 6), _p("NOT FOR SUBMISSION. This design uses rules values not yet verified against the current standards: "
                               + ", ".join(unverified(pkg)) + ".", WARN)]

    # 1. Design basis
    s += [Paragraph("1. Design basis", H2),
          _p("Voltage drop by the Herman-Beta statistical method (NRS 034-1) at the rules' confidence level; thermal loading from the same "
             "design currents; fault levels by IEC 60909-0; overhead mechanics by SANS 10280-1; underground derating per SANS 10198-4. "
             "Costs are indicative estimates from the stated rate list, not tender prices. Each check below names its clause and the "
             "standards index id (docs/standards-index.md).", BODY)]
    if pkg.connection_point:
        cp = pkg.connection_point
        s.append(table([["Connection point", "Value"], ["Location", f"{cp.get('lon'):.6f}, {cp.get('lat'):.6f}"], ["Voltage", f"{cp.get('voltage_kv')} kV"],
                        ["Available capacity", f"{_fmt(cp.get('available_capacity_kva'), 0)} kVA"], ["Fault level max / min", f"{cp.get('fault_mva_max')} / {cp.get('fault_mva_min') or '—'} MVA"],
                        ["Authority reference", cp.get("reference") or "—"]], [60 * mm, W - 60 * mm]))

    # 2. Loads
    s.append(Paragraph("2. Loads", H2))
    if pkg.loads:
        by_class: dict[str, list] = {}
        for r in pkg.loads:
            by_class.setdefault(r.load_class or r.kind, []).append(r)
        s.append(table([["Load class", "Buildings", "Three-phase", "Overridden", "Sum of ADMD (kVA)"]] +
                       [[k, len(v), sum(1 for r in v if r.phases == 3), sum(1 for r in v if r.overridden), _fmt(sum(r.kva or 0 for r in v), 1)]
                        for k, v in sorted(by_class.items())], [55 * mm, 25 * mm, 25 * mm, 25 * mm, W - 130 * mm]))
        s.append(_p("The full load schedule is issued as a spreadsheet with this report.", BODY))
    else:
        s.append(_p("No load schedule in this package.", BODY))

    # 3. LV design
    s.append(Paragraph("3. LV design", H2))
    for site in pkg.lv_designs:
        o = chosen_option(site)
        if o is None:
            continue
        a = o["analysis"]
        s.append(Paragraph(f"3.{pkg.lv_designs.index(site) + 1} {site.label} – {o['construction']}", H3))
        comp = site.result.get("comparison", [])
        if comp:
            s.append(table([["Construction", "Passes", "Worst ΔV %", "Max loading %", "Transformer kVA", "Poles", "Kiosks", "Cost"]] +
                           [[c["construction"], "yes" if c["passed"] else "NO", _fmt(c["worst_vdrop_pct"]), _fmt(c["max_loading_pct"], 0), _fmt(c["transformer_kva"], 0),
                             c["poles"], c["kiosks"], f"{c['currency']} {c['cost_total']:,.0f}"] for c in comp]))
            s.append(Spacer(1, 4))
        s.append(table([["Value", "Result", "Formula id", "Formula", "Clause", "Inputs"],
                        traced_rows("Design demand", a["demand_kva"]), traced_rows("Worst voltage drop", a["worst_vdrop_pct"]),
                        traced_rows("Maximum LV fault", a["max_fault_ka"])], [24 * mm, 20 * mm, 24 * mm, 50 * mm, 26 * mm, W - 144 * mm]))
        feeders = [b for b in a["branches"] if any(x["id"] == b["id"] and x["kind"] == "feeder" for x in o["network"]["branches"])]
        if feeders:
            s.append(Spacer(1, 4))
            s.append(table([["Branch", "Conductor", "Length m", "Design A", "Rating A", "Derating", "Loading %", "Customers"]] +
                           [[b["id"], b["conductor"], _fmt(b["length_m"], 1), _fmt(b["design_current_a"], 1), _fmt(b["rating_a"], 0), _fmt(b["derating"]),
                             _fmt(b["loading_pct"], 1), b["customers"]] for b in feeders]))
        failed = [c for c in a["checks"] if not c["passed"]]
        s.append(_p(f"{len(a['checks'])} checks, {len(failed)} failed. Cost {o['cost']['currency']} {o['cost']['total']:,.0f} (indicative).", BODY))

    # 4. MV design
    if pkg.mv_design:
        mv = pkg.mv_design.result
        s += [PageBreak(), Paragraph("4. MV design", H2)]
        s.append(table([["Site", "Unit", "Rating kVA", "Design kVA", "MV ΔV %", "Regulation %", "Tap %", "Customer V %"]] +
                       [[x["placement"]["site_id"], x["placement"]["unit"], _fmt(x["placement"]["rating_kva"], 0), _fmt(x["placement"]["design_kva"], 1),
                         _fmt(x["mv_vdrop_pct"]), _fmt(x["regulation_pct"]), _fmt(x["tap_pct"], 1),
                         f"{_fmt(x['v_min_pct'], 1)}–{_fmt(x['v_max_pct'], 1)}"] for x in mv.get("sites", [])]))
        if mv.get("mv_analysis"):
            s.append(Spacer(1, 4))
            s.append(table([["Branch", "Conductor", "Length m", "Demand kVA", "Current A", "Loading %", "ΔV at end %"]] +
                           [[b["id"], b["conductor"], _fmt(b["length_m"], 0), _fmt(b["demand_kva"], 0), _fmt(b["current_a"], 1), _fmt(b["loading_pct"], 0),
                             _fmt(b["vdrop_pct_end"])] for b in mv["mv_analysis"]["branches"] if b["length_m"] >= 1]))
        s.append(_p(f"MV design {'passes every check' if mv.get('passed') else 'FAILS checks'}; cost {mv.get('currency', '')} {mv.get('cost_total', 0):,.0f} (indicative).", BODY))

    # 5. Bulk supply
    if pkg.bulk_study:
        b = pkg.bulk_study.result
        s += [Paragraph("5. Bulk supply", H2),
              table([["Item", "Value"], ["Demand at the connection point", f"{_fmt(b['supply_kva'], 1)} kVA ({_fmt(b['supply_kw'], 1)} kW)"],
                     ["Available capacity", f"{_fmt(b['available_capacity_kva'], 0)} kVA"], ["Notified maximum demand", f"{_fmt(b['notified_max_demand_kva'], 0)} kVA"],
                     ["Losses", f"{_fmt(b['losses_kw'], 2)} kW"]], [70 * mm, W - 70 * mm]), Spacer(1, 4),
              table([["Bus", "kV", "V %", "I\"k3 max kA", "I\"k1 min kA"]] +
                    [[x["id"], _fmt(x["vn_kv"]), _fmt(x["v_pct"]), _fmt(x["ikss3_max_ka"]), _fmt(x["ikss1_min_ka"])] for x in b["buses"]])]
        for a_ in b.get("assumptions", []):
            s.append(_p(f"Assumption: {a_}", BODY))

    # 6. Option comparison
    if pkg.option_search:
        r = pkg.option_search.result
        s.append(Paragraph("6. Options considered", H2))
        s.append(table([["Option", "Construction", "Transformer", "Capex", "Lifetime cost", "Spare %", "Too close to call"]] +
                       [[o["title"], o["design"]["construction"], f"{o['option']['analysis']['transformer_kva']:g} kVA at the {o['design']['position_label']}",
                         f"{r['currency']} {o['option']['cost']['total']:,.0f}", f"{r['currency']} {o['lifetime']['total']['value']:,.0f}", _fmt(o["spare"]["spare_pct"], 1),
                         "yes" if o["too_close_to_call"] else ""] for o in r["options"]]))

    # 7. Assumptions
    s.append(Paragraph("7. Assumptions", H2))
    if pkg.assumptions:
        s.append(table([["Assumption", "Source", "Status"]] + [[a.text, a.source or "", a.status] for a in pkg.assumptions], [W - 60 * mm, 35 * mm, 25 * mm]))
    else:
        s.append(_p("None recorded.", BODY))

    # 8. Traceability
    s += [PageBreak(), Paragraph("8. Traceability: every check", H2)]
    rows = [["Where", "Check", "Subject", "Value", "Limit", "Result", "Clause"]]
    for site in pkg.lv_designs:
        o = chosen_option(site)
        if o:
            rows += check_rows(o["analysis"]["checks"], site.label)
    if pkg.mv_design:
        rows += check_rows(pkg.mv_design.result.get("checks", []), "MV")
    if pkg.bulk_study:
        rows += check_rows(pkg.bulk_study.result.get("checks", []), "Bulk")
    s.append(table(rows, [22 * mm, 26 * mm, 30 * mm, 22 * mm, 22 * mm, 12 * mm, W - 134 * mm]))
    if sheets:
        s.append(KeepTogether([Paragraph("Drawings issued with this report", H3),
                               table([["Number", "Title", "Scale"]] + [[x.number, x.title, f"1:{x.scale}"] for x in sheets])]))
    doc.build(s, onFirstPage=on_page, onLaterPages=on_page)
    return buf.getvalue()
