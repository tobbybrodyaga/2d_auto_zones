from __future__ import annotations

from dataclasses import asdict
from typing import Dict, List, Literal, Optional, Tuple

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

from rebar_extra_reinforcement_engine import (
    AxisLine,
    CostModel,
    Hole,
    RNFAttributes,
    RebarExtraReinforcementEngine,
    SlabGeometry,
    SliderConfig,
)


class SlabGeometryIn(BaseModel):
    thickness_mm: float
    cover_top_mm: float
    cover_bottom_mm: float
    concrete_class: Literal["B15", "B20", "B25", "B30", "B35", "B40"]


class RNFAttributesIn(BaseModel):
    rnfc_division: str
    rnfc_design_mark: str
    rnfc_assembly_mark: str
    rnfc_element_mark: str
    alpha: float


class HoleIn(BaseModel):
    xmin_mm: float
    ymin_mm: float
    xmax_mm: float
    ymax_mm: float
    width_mm: float
    height_mm: float


class AxisLineIn(BaseModel):
    name: str
    coord_mm: float
    direction: Literal["X", "Y"]


class CostModelIn(BaseModel):
    steel_price_per_kg: float = 95.0
    complexity_penalty_per_point: float = 120.0
    zone_count_weight: float = 1.0
    overlap_joint_weight: float = 0.5
    bent_zone_weight: float = 1.5
    t_min_2: float = 20.0
    t_2_3: float = 40.0
    t_3_4: float = 70.0
    t_4_max: float = 110.0


class SliderConfigIn(BaseModel):
    edge_exact_medium: float = 0.34
    edge_medium_coarse: float = 0.67


class LayoutRequest(BaseModel):
    # Lira-like grid: As (mm2) in each 300x300 cell
    area_grid: List[List[float]]
    direction: Literal["X", "Y"]
    current_row: int = Field(ge=1, le=4)
    row_diameters_mm: Optional[Dict[int, int]] = None
    bar_step_perp_mm: int = Field(default=200)
    origin_x_mm: float = 0.0
    origin_y_mm: float = 0.0
    slab_bbox: Optional[Tuple[float, float, float, float]] = None
    slab: SlabGeometryIn
    rnf: RNFAttributesIn
    holes: List[HoleIn] = []
    axes: List[AxisLineIn] = []
    slider_value: float = 0.0  # 0..1 (min..max)
    slider_cfg: SliderConfigIn = SliderConfigIn()
    cost_model: CostModelIn = CostModelIn()
    auto_snap_to_axis_10mm: bool = True


class DwgPreExtractedRequest(BaseModel):
    """
    API format for external DWG parser output.
    This keeps API independent from DWG parsing stack.
    """

    plate_boundary: List[Tuple[float, float]]
    nodes_xy: List[Tuple[float, float]]
    node_values_as_mm2: List[float]
    grid_spacing_mm: int = 300
    # Reuse layout settings
    layout: LayoutRequest


app = FastAPI(title="Rebar Layout API", version="0.1.0")


def _to_engine_objects(req: LayoutRequest):
    slab = SlabGeometry(
        thickness_mm=req.slab.thickness_mm,
        cover_top_mm=req.slab.cover_top_mm,
        cover_bottom_mm=req.slab.cover_bottom_mm,
        concrete_class=req.slab.concrete_class,
    )
    rnfc = RNFAttributes(
        rnfc_division=req.rnf.rnfc_division,
        rnfc_design_mark=req.rnf.rnfc_design_mark,
        rnfc_assembly_mark=req.rnf.rnfc_assembly_mark,
        rnfc_element_mark=req.rnf.rnfc_element_mark,
        alpha=req.rnf.alpha,
    )
    holes = [
        Hole(
            xmin_mm=h.xmin_mm,
            ymin_mm=h.ymin_mm,
            xmax_mm=h.xmax_mm,
            ymax_mm=h.ymax_mm,
            width_mm=h.width_mm,
            height_mm=h.height_mm,
        )
        for h in req.holes
    ]
    axes = [
        AxisLine(name=a.name, coord_mm=a.coord_mm, direction=a.direction)
        for a in req.axes
    ]
    cost = CostModel(
        steel_price_per_kg=req.cost_model.steel_price_per_kg,
        complexity_penalty_per_point=req.cost_model.complexity_penalty_per_point,
        zone_count_weight=req.cost_model.zone_count_weight,
        overlap_joint_weight=req.cost_model.overlap_joint_weight,
        bent_zone_weight=req.cost_model.bent_zone_weight,
        t_min_2=req.cost_model.t_min_2,
        t_2_3=req.cost_model.t_2_3,
        t_3_4=req.cost_model.t_3_4,
        t_4_max=req.cost_model.t_4_max,
    )
    slider_cfg = SliderConfig(
        edge_exact_medium=req.slider_cfg.edge_exact_medium,
        edge_medium_coarse=req.slider_cfg.edge_medium_coarse,
    )
    return slab, rnfc, holes, axes, cost, slider_cfg


@app.get("/health")
def health():
    return {"ok": True}


@app.post("/layout/evaluate-slider")
def evaluate_slider(req: LayoutRequest):
    if not req.area_grid or not req.area_grid[0]:
        raise HTTPException(status_code=400, detail="area_grid is empty")

    engine = RebarExtraReinforcementEngine(grid_spacing_mm=300)
    slab, rnfc, holes, axes, cost, slider_cfg = _to_engine_objects(req)
    max_d = engine.max_additional_diameter_from_covers(slab)

    result = engine.evaluate_slider_and_relayout(
        slider_value=req.slider_value,
        slider_cfg=slider_cfg,
        area_grid=req.area_grid,
        direction=req.direction,
        max_diameter_mm=max_d,
        slab=slab,
        rnfc=rnfc,
        bar_step_perp_mm=req.bar_step_perp_mm,
        origin_x_mm=req.origin_x_mm,
        origin_y_mm=req.origin_y_mm,
        cost_model=cost,
        holes=holes,
        slab_bbox=req.slab_bbox,
        current_row=req.current_row,
        row_diameters_mm=req.row_diameters_mm,
        axis_lines=axes if axes else None,
        auto_snap_to_axis_10mm=req.auto_snap_to_axis_10mm,
    )

    return {
        "slider": {
            "value": result.slider_value,
            "label": result.slider_label,
            "selected_detail_level": result.selected_detail_level,
        },
        "metrics": {
            "complexity_score": result.complexity_score,
            "complexity_scale": result.complexity_scale,
            "economic_score": result.economic_score,
            "steel_cost": result.steel_cost,
            "total_cost": result.total_cost,
        },
        "zones": [asdict(z) for z in result.zones],
    }


@app.post("/layout/from-dwg-pre-extracted")
def layout_from_pre_extracted(req: DwgPreExtractedRequest):
    """
    Endpoint for pipeline:
        DWG parser (external) -> this API.
    For now it expects ready area_grid in req.layout.
    """
    # Here you can add node interpolation into area_grid if needed:
    # req.nodes_xy + req.node_values_as_mm2 -> raster to grid.
    return evaluate_slider(req.layout)

