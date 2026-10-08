"""Design report as PDF (plan 6.3): every result, the assumptions, and the traceability behind each number.

Sections: status and summary; the engineer's approved text sections; assumptions, placeholders and elements not
inspected; issues; LV feeders and conductors; overhead line; underground cables; transformers; MV network; bulk
supply; cost; the option comparison. Appendix A lists every check with its value, limit, clause and formula;
appendix B every traced value with its formula, clause, rules hash and named inputs. Each page carries the stamp
in its footer and, when the design is not fit to submit, says so across its head.
"""

from __future__ import annotations

import io
from collections import Counter

from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfgen import canvas as canvas_mod
from reportlab.platypus import KeepTogether, PageBreak, Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle

from ..design.run import Design
from . import DocumentRequest
from .common import FONT, FONT_BOLD, NOT_FIT, esc, money, num, pdf_fonts, pdf_text, stamp, traces

RED = colors.HexColor("#c8102e")
GREEN = colors.HexColor("#00792b")
INK = colors.HexColor("#1d2329")
RULE = colors.HexColor("#c9ced4")
BAND = colors.HexColor("#f2f4f6")


def styles() -> dict[str, ParagraphStyle]:
    pdf_fonts()
    base = ParagraphStyle("body", fontName=FONT, fontSize=8.5, leading=11, textColor=INK, alignment=TA_LEFT)
    return {
        "body": base,
        "small": ParagraphStyle("small", parent=base, fontSize=7, leading=8.6),
        "cell": ParagraphStyle("cell", parent=base, fontSize=7, leading=8.4),
        "h1": ParagraphStyle("h1", parent=base, fontName=FONT_BOLD, fontSize=15, leading=19, spaceBefore=6, spaceAfter=6),
        "h2": ParagraphStyle("h2", parent=base, fontName=FONT_BOLD, fontSize=11, leading=14, spaceBefore=10, spaceAfter=4),
        "h3": ParagraphStyle("h3", parent=base, fontName=FONT_BOLD, fontSize=9, leading=12, spaceBefore=6, spaceAfter=2),
        "status": ParagraphStyle("status", parent=base, fontName=FONT_BOLD, fontSize=10, leading=13, textColor=colors.white),
    }


def table(rows: list[list], st: dict, widths: list[float] | None = None, fails: list[int] = ()) -> Table:
    """A table whose first row is the header; `fails` are row indexes (in the body) drawn in red."""
    cell = st["cell"]
    data = [[c if isinstance(c, Paragraph) else Paragraph(esc(c), cell) for c in r] for r in rows]
    t = Table(data, colWidths=widths, repeatRows=1)
    style = [("FONT", (0, 0), (-1, 0), FONT_BOLD, 7), ("BACKGROUND", (0, 0), (-1, 0), BAND), ("LINEBELOW", (0, 0), (-1, 0), 0.6, INK),
             ("LINEBELOW", (0, 1), (-1, -1), 0.25, RULE), ("VALIGN", (0, 0), (-1, -1), "TOP"),
             ("TOPPADDING", (0, 0), (-1, -1), 1.5), ("BOTTOMPADDING", (0, 0), (-1, -1), 1.5)]
    for i in fails:
        style.append(("TEXTCOLOR", (0, i + 1), (-1, i + 1), RED))
    t.setStyle(TableStyle(style))
    return t


def kv(pairs: list[tuple[str, object]], st: dict, width: float = 170 * mm) -> Table:
    data = [[Paragraph(esc(k), st["cell"]), Paragraph(esc(num(v) if not isinstance(v, str) else v), st["cell"])] for k, v in pairs]
    t = Table(data, colWidths=[55 * mm, width - 55 * mm])
    t.setStyle(TableStyle([("LINEBELOW", (0, 0), (-1, -1), 0.25, RULE), ("VALIGN", (0, 0), (-1, -1), "TOP"),
                           ("FONT", (0, 0), (0, -1), FONT_BOLD, 7)]))
    return t


def _numbered(footer: str, unfit: bool):
    class NumberedCanvas(canvas_mod.Canvas):
        def __init__(self, *a, **k):
            super().__init__(*a, **k)
            self._pages = []

        def showPage(self):
            self._pages.append(dict(self.__dict__))
            self._startPage()

        def save(self):
            total = len(self._pages)
            for state in self._pages:
                self.__dict__.update(state)
                self._decorate(total)
                super().showPage()
            super().save()

        def _decorate(self, total: int) -> None:
            w, h = A4
            self.setFont(FONT, 6.5)
            self.setFillColor(INK)
            first, _, second = footer.partition("\n")
            self.drawString(15 * mm, 9.5 * mm, pdf_text(first)[:150])
            self.drawString(15 * mm, 6.5 * mm, pdf_text(second)[:170])
            self.drawRightString(w - 15 * mm, 9.5 * mm, f"page {self._pageNumber} of {total}")
            self.setStrokeColor(RULE)
            self.line(15 * mm, 12.5 * mm, w - 15 * mm, 12.5 * mm)
            if unfit:
                self.setFont(FONT_BOLD, 8)
                self.setFillColor(RED)
                self.drawCentredString(w / 2, h - 9 * mm, NOT_FIT)

    return NumberedCanvas


def _status(s, st: dict) -> Table:
    text = esc(s.status)
    t = Table([[Paragraph(text, st["status"])]], colWidths=[180 * mm])
    t.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, -1), RED if not s.fit else GREEN), ("TOPPADDING", (0, 0), (-1, -1), 5),
                           ("BOTTOMPADDING", (0, 0), (-1, -1), 5), ("LEFTPADDING", (0, 0), (-1, -1), 6)]))
    return t


def _lv(d: Design, st: dict) -> list:
    out = [Paragraph("LV network", st["h2"])]
    a = d.lv.analysis
    out.append(Paragraph(esc(f"Voltage drop limit {num(a.limit_pct)} % of {num(a.phase_voltage_v)} V; Herman-Beta at "
                             f"{num(a.confidence_pct)} % confidence. {d.lv.generated} poles or kiosks placed by the design."), st["body"]))
    rows = [["Feeder", "Conductor", "Max drop %", "At", "Max loading %", "Branch", "Min fault A", "At", "Fuse A", "Pass"]]
    fails = []
    for i, f in enumerate(a.feeders):
        fuse = "–" if f.design_current_a is None else f"{num(f.fuse_a, 0)}, needs {num(f.min_fault_required_a, 0)}" if f.fuse_a else "none fits"
        rows.append([f.feeder, d.lv.sizing.feeders.get(f.feeder, "–"), num(f.max_drop_pct, 3), f.max_drop_at, num(f.max_utilisation_pct, 1),
                     f.max_utilisation_branch, num(f.min_fault_a, 0), f.min_fault_at, fuse, num(f.passes)])
        if not f.passes:
            fails.append(i)
    out.append(table(rows, st, fails=fails))
    if a.protection:
        out.append(Paragraph(esc(f"Each feeder has the smallest gG fuse at or above its design current and not above its conductor's "
                                 f"rating; the least fault on the feeder must be at least the current it needs. {a.protection.clause}"), st["small"]))
    if d.lv.sizing.steps:
        out.append(Paragraph("Conductor sizing steps", st["h3"]))
        out.append(table([["Group", "Conductor", "Reason"]] + [[x.group, x.conductor, x.reason] for x in d.lv.sizing.steps], st))
    al = d.lv.allocation.summary
    out.append(Paragraph("Loads", st["h3"]))
    out.append(kv([("Loads", al.loads), ("Connected", al.allocated), ("Not connected", al.unallocated), ("Without an estimate", al.unestimated),
                   ("Three-phase", al.three_phase), ("Boxes", al.boxes), ("Connected kVA", al.allocated_kva),
                   ("Longest service, m", al.longest_service_m)], st))
    if d.services:
        out += _services(d, st)
    return out


def _services(d: Design, st: dict) -> list:
    sv = d.services
    out = [Paragraph("Services", st["h3"]), Paragraph(esc(sv.clause), st["small"])]
    by = Counter(x.conductor for x in sv.services)
    rows = [["Conductor", "Services", "Length m", "Worst drop %", "Over limit"]]
    for code, n in sorted(by.items()):
        xs = [x for x in sv.services if x.conductor == code]
        rows.append([code, n, num(sum(x.length_m for x in xs), 1), num(max(x.drop_pct for x in xs), 3), sum(1 for x in xs if not x.passes)])
    out.append(table(rows, st, fails=[i for i, r in enumerate(rows[1:], 1) if r[4]]))
    strung = [x for x in sv.services if x.clearance_m is not None]
    pairs = [("Service drop limit, %", sv.limit_pct), ("Worst service drop, %", sv.worst_drop.value if sv.worst_drop else None)]
    if strung:
        pairs += [("Service poles added", f"{sv.service_poles} × {num(sv.pole_height_m)} m"),
                  ("Services with service poles", sum(1 for x in strung if x.poles)),
                  ("Lowest service clearance, m", f"{num(min(x.clearance_m for x in strung), 2)} (at least {num(sv.min_clearance_m)})"),
                  ("Services that do not clear", ", ".join(x.label or x.load_id for x in strung if x.clears is False) or "none")]
    out.append(kv(pairs, st))
    return out


def _overhead(d: Design, st: dict) -> list:
    out = []
    for title, res in (("Overhead line, LV", d.overhead), ("Overhead line, MV", d.mv_overhead)):
        if res is None:
            continue
        out.append(Paragraph(title, st["h2"]))
        out.append(Paragraph(esc(res.clause), st["small"]))
        rows = [["Section", "Conductor", "Spans", "Ruling span m", "Everyday kN", "Hot kN", "Cold kN", "Limit kN", "Pass"]]
        fails = [i for i, s in enumerate(res.sections) if not s.passes]
        rows += [[s.id, s.conductor, s.spans, num(s.ruling_span_m, 1), num(s.everyday_tension_kn, 3), num(s.hot_tension_kn, 3),
                  num(s.cold_tension_kn, 3), num(s.max_tension_kn, 2), num(s.passes)] for s in res.sections]
        out.append(table(rows, st, fails=fails))
        poles = Counter((p.height_m, p.pole_class or "–") for p in res.poles if p.kind != "source")
        out.append(Paragraph("Poles by height and class", st["h3"]))
        out.append(table([["Height m", "Class", "Count"]] + [[num(h), c, n] for (h, c), n in sorted(poles.items(), key=lambda x: (x[0][0], str(x[0][1])))], st))
        failed = [p for p in res.poles if not p.passes] + [s for s in res.spans if not s.passes]
        out.append(kv([("Stays", sum(p.stays for p in res.poles)), ("Poles placed by the design", sum(1 for p in res.poles if p.generated)),
                       ("Worst sag, m", res.worst_sag.value if res.worst_sag else None),
                       ("Lowest clearance, m", res.worst_clearance.value if res.worst_clearance else None),
                       ("Failing poles and spans", ", ".join(x.id for x in failed) or "none")], st))
    return out


def _underground(d: Design, st: dict) -> list:
    u = d.underground
    if u is None:
        return []
    out = [Paragraph("Underground cables", st["h2"]), Paragraph(esc(u.clause), st["small"])]
    out.append(kv([(k.replace("_", " "), v) for k, v in u.conditions.items()] + [("Kiosks", u.kiosks)], st))
    rows = [["Branch", "Cable", "Laid", "Base A", "Soil", "Depth", "Temp", "Group", "Factor", "Rating A"]]
    rows += [[r.branch, r.conductor, r.installation, num(r.base_a, 0), num(r.soil, 3), num(r.depth, 3), num(r.temperature, 3), r.group,
              num(r.grouping, 3), num(r.derated_a, 1)] for r in u.ratings]
    out.append(table(rows, st))
    return out


def _transformers(d: Design, st: dict) -> list:
    tx = d.transformers
    out = [Paragraph("Transformers", st["h2"]), Paragraph(esc(tx.clause), st["small"])]
    rows = [["Transformer", "Marked", "Mounting", "Feeders", "Loads", "Demand kVA", "Rating kVA", "Loading %", "Spare kVA", "Z %", "Pass"]]
    fails = [i for i, t in enumerate(tx.transformers) if not t.passes]
    rows += [[t.label or t.id, t.marked_kind, t.mounting, t.feeders, t.loads, num(t.demand_kva), num(t.rating_kva), num(t.loading_pct, 1),
              num(t.spare_kva), num(t.impedance_pct), num(t.passes)] for t in tx.transformers]
    out.append(table(rows, st, fails=fails))
    return out


def _mv(d: Design, st: dict) -> list:
    m = d.mv
    if m is None:
        return [Paragraph("MV network", st["h2"]), Paragraph("The MV network was not designed: see the issues.", st["body"])]
    out = [Paragraph("MV network", st["h2"]),
           Paragraph(esc(f"{m.construction}, {num(m.voltage_kv)} kV, drop limit {num(m.limit_pct)} %, total {num(m.total_kva)} kVA."), st["body"])]
    rows = [["Transformer", "Feeder", "Distance m", "MV drop %", "Regulation %", "Tap %", "LV full load %", "LV no load %", "Pass"]]
    fails = [i for i, t in enumerate(m.taps) if not t.passes]
    rows += [[t.label or t.id, t.feeder or "–", num(t.distance_m, 0), num(t.drop_pct, 4), num(t.regulation_pct, 3), num(t.tap_pct),
              num(t.lv_full_load_pct, 2), num(t.lv_no_load_pct, 2), num(t.passes)] for t in m.taps]
    out.append(table(rows, st, fails=fails))
    used = Counter(b.conductor for b in m.branches)
    out.append(kv([("Conductors", ", ".join(f"{c} ({n} sections)" for c, n in used.items()) or "–"),
                   ("Highest loading %", max((b.utilisation_pct for b in m.branches), default=None))], st))
    return out


def _bulk(d: Design, st: dict) -> list:
    b = d.bulk
    out = [Paragraph("Bulk supply", st["h2"]), Paragraph(esc(b.clause), st["small"])]
    if b.stopped:
        out.append(Paragraph(esc(f"Not run: {b.stopped}"), st["body"]))
    if b.supply:
        s = b.supply
        out.append(kv([("Project demand, kVA", s.demand_kva), ("Required with growth, kVA", s.required_kva),
                       ("Notified maximum demand, kVA", s.nmd_kva), ("Connection point capacity, kVA", s.capacity_kva),
                       ("Within capacity", s.passes)], st))
    if not b.stopped:
        mv = [x for x in b.buses if x.level == "mv"]
        out.append(kv([("Load flow converged", b.converged), ("Lowest voltage, pu", b.min_vm_pu), ("Highest voltage, pu", b.max_vm_pu),
                       ("Highest MV fault, kA", max((x.ik3_max_ka or 0 for x in mv), default=None))], st))
        rows = [["Transformer", "Loading %", "LV voltage pu", "LV 3-phase fault kA"]]
        rows += [[t.label or "–", num(t.loading_pct, 1), num(t.lv_vm_pu, 4), num(t.ik3_lv_ka, 3)] for t in b.transformers]
        out.append(table(rows, st))
        weak = [x for x in b.branches if not x.passes]
        if weak:
            out.append(Paragraph("Branches failing loading or fault withstand", st["h3"]))
            out.append(table([["Branch", "Level", "Conductor", "Loading %", "Fault kA", "Withstand kA"]] +
                             [[x.id, x.level, x.conductor, num(x.loading_pct, 1), num(x.ik_max_ka, 3), num(x.withstand_ka, 3)] for x in weak],
                             st, fails=list(range(len(weak)))))
    return out


def _cost(d: Design, st: dict) -> list:
    c = d.cost
    out = [Paragraph("Cost", st["h2"])]
    if c.indicative:
        out.append(Paragraph(esc(f"ESTIMATE: indicative rates from '{c.library}' ({c.rate_date}); not a price."), st["body"]))
    groups: dict[str, list] = {}
    for line in c.lines:
        groups.setdefault(line.group, []).append(line)
    rows = [["Group", "Lines", "Amount", "Low", "High"]]
    for g, lines in groups.items():
        rows.append([g, len(lines), money(sum(x.amount or 0 for x in lines), c.currency), money(sum(x.low or 0 for x in lines), c.currency),
                     money(sum(x.high or 0 for x in lines), c.currency)])
    rows.append(["Total", len(c.lines), money(c.capex, c.currency), money(c.capex_low, c.currency), money(c.capex_high, c.currency)])
    out.append(table(rows, st))
    loss = c.losses
    e = c.economics
    out.append(kv([("Losses at design demand, kW", (f"LV {num(loss.lv_kw, 3)}, MV {num(loss.mv_kw, 3)}, transformer load "
                                                     f"{num(loss.transformer_load_kw, 3)}, no-load {num(loss.transformer_no_load_kw, 3)}")),
                   ("Loss energy, MWh a year", loss.energy_mwh_per_year), ("Present value of losses", money(loss.npv, c.currency)),
                   ("Lifetime cost", f"{money(c.lifetime, c.currency)} ({money(c.lifetime_low, c.currency)} to {money(c.lifetime_high, c.currency)})"),
                   ("Economics", (f"{e.period_years} years at {num(e.discount_rate_pct)} %, {num(e.energy_cost_zar_per_kwh)} {c.currency}/kWh, "
                                  f"growth {num(e.load_growth_pct)} %, loss load factor {num(e.loss_load_factor)}"))], st))
    return out


def render(req: DocumentRequest) -> bytes:
    d, meta = req.design, req.meta
    s = stamp(meta, d)
    st = styles()
    buf = io.BytesIO()
    doc = SimpleDocTemplate(buf, pagesize=A4, leftMargin=15 * mm, rightMargin=15 * mm, topMargin=15 * mm, bottomMargin=17 * mm,
                            title=pdf_text(f"{s.project} design report {s.revision}"), author=pdf_text(meta.engineer_name or "Reticula"),
                            subject=pdf_text(s.status), creator="Reticula calc service")
    story: list = [Paragraph(esc("Electrical reticulation design report"), st["h1"]), Paragraph(esc(s.project), st["h2"]),
                   kv(s.lines()[1:-1] + [("Client", meta.client or "–"), ("Area", meta.area or "–"), ("Document", meta.document_number or "–")], st),
                   Spacer(1, 4 * mm), _status(s, st)]

    sm = d.summary
    story += [Paragraph("Summary", st["h2"]), kv([
        ("Construction", sm.construction), ("Loads (connected)", f"{sm.loads} ({sm.connected})"),
        ("Transformers", f"{sm.transformers}, {num(sm.transformer_kva)} kVA, {num(sm.spare_pct, 1)} % spare"),
        ("Poles, stays, kiosks", f"{sm.poles}, {sm.stays}, {sm.kiosks}"), ("LV and MV route, km", f"{num(sm.lv_km, 3)} and {num(sm.mv_km, 3)}"),
        ("Worst LV drop, %", sm.worst_lv_drop_pct), ("Worst MV drop, %", sm.worst_mv_drop_pct), ("Notified maximum demand, kVA", sm.nmd_kva),
        ("Capital cost", money(sm.capex, d.cost.currency)), ("Lifetime cost", money(sm.lifetime, d.cost.currency)),
        ("Checks (failed)", f"{sm.checks} ({sm.failures})")], st)]

    story.append(Paragraph("Engineer's notes", st["h2"]))
    if req.sections:
        for sec in req.sections:
            story.append(Paragraph(esc(sec.title), st["h3"]))
            story += [Paragraph(esc(par), st["body"]) for par in sec.text.split("\n\n") if par.strip()]
    else:
        story.append(Paragraph("No approved text sections.", st["body"]))

    story.append(Paragraph("Assumptions, placeholders and inspection", st["h2"]))
    story += [Paragraph(esc(f"• {p}"), st["body"]) for p in d.placeholders] or [Paragraph("No placeholder values.", st["body"])]
    if d.not_inspected:
        story.append(Paragraph("Proposed elements not yet inspected", st["h3"]))
        story.append(table([["Candidate", "Kind", "Label", "Elements"]] +
                           [[n.candidate_id, n.kind, n.label or "–", ", ".join(n.elements[:12]) + (" …" if len(n.elements) > 12 else "")]
                            for n in d.not_inspected], st))
    story.append(Paragraph("Issues", st["h3"]))
    story.append(table([["Severity", "Code", "Message", "Count", "Examples"]] +
                       [[i.severity, i.code, i.message, i.count, ", ".join(i.samples[:5])] for i in d.issues], st,
                       widths=[16 * mm, 30 * mm, 94 * mm, 12 * mm, 28 * mm], fails=[k for k, i in enumerate(d.issues) if i.severity == "error"]))

    story += _lv(d, st) + _overhead(d, st) + _underground(d, st) + _transformers(d, st) + _mv(d, st) + _bulk(d, st) + _cost(d, st)
    if d.comparison:
        story.append(Paragraph("Construction options compared", st["h2"]))
        story.append(table([["Construction", "Capital", "Low", "High", "Lifetime", "Worst LV drop %", "Spare %", "Failures", "Chosen", "Too close"]] +
                           [[o.construction, money(o.capex), money(o.capex_low), money(o.capex_high), money(o.lifetime), num(o.worst_lv_drop_pct, 3),
                             num(o.spare_pct, 1), o.failures, num(o.chosen), num(o.too_close)] for o in d.comparison], st))

    story += [PageBreak(), Paragraph("Appendix A. Checks", st["h1"]),
              Paragraph(esc(f"{len(d.checks)} checks, {sum(1 for c in d.checks if not c.passes)} failing (in red)."), st["body"])]
    rows = [["Check", "Element", "Value", "Limit", "Unit", "Pass", "Clause", "Formula"]]
    rows += [[c.id, c.label or c.element, num(c.value, 4), num(c.limit, 4), c.unit, num(c.passes), (c.index + ": " if c.index else "") + c.clause,
              c.formula_id or "–"] for c in d.checks]
    story.append(table(rows, st, widths=[30 * mm, 22 * mm, 15 * mm, 15 * mm, 9 * mm, 9 * mm, 52 * mm, 28 * mm],
                       fails=[i for i, c in enumerate(d.checks) if not c.passes]))

    story += [PageBreak(), Paragraph("Appendix B. Traceability", st["h1"]),
              Paragraph("Every calculated value in this design, with its formula, the clause it answers to and its named inputs.", st["body"])]
    for path, t in traces(d):
        block = [Paragraph(esc(f"{t.formula_id}: {num(t.value, 4)} {t.unit}"), st["h3"]), Paragraph(esc(f"at {path}"), st["small"]),
                 Paragraph(esc(t.formula), st["small"]), Paragraph(esc(f"Clause: {t.clause or '–'} · rules hash {t.rules_hash}"), st["small"])]
        if t.inputs:
            block.append(table([["Input", "Value", "Unit", "Source"]] + [[i.name, num(i.value, 4) if not isinstance(i.value, str) else i.value,
                                                                           i.unit, i.source] for i in t.inputs], st,
                               widths=[30 * mm, 30 * mm, 18 * mm, 102 * mm]))
        story.append(KeepTogether(block))
    doc.build(story, canvasmaker=_numbered(s.footer(), not s.fit))
    return buf.getvalue()
