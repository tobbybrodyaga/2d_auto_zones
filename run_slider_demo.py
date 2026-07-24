from rebar_extra_reinforcement_engine import (
    RebarExtraReinforcementEngine,
    SlabGeometry,
    RNFAttributes,
    Hole,
    AxisLine,
    CostModel,
    SliderConfig,
)


def main() -> None:
    engine = RebarExtraReinforcementEngine(grid_spacing_mm=300)

    # Demo mosaic As (mm2) on 300x300 grid
    area_grid = [
        [0, 0, 120, 180, 120, 0, 0, 0, 0, 0],
        [0, 150, 260, 320, 200, 80, 0, 0, 0, 0],
        [0, 120, 220, 280, 170, 90, 0, 0, 0, 0],
        [0, 80, 140, 180, 130, 60, 0, 0, 0, 0],
        [0, 0, 90, 110, 85, 0, 0, 0, 0, 0],
    ]

    slab = SlabGeometry(
        thickness_mm=240,
        cover_top_mm=25,
        cover_bottom_mm=25,
        concrete_class="B25",
    )

    rnfc = RNFAttributes(
        rnfc_division="KJ",
        rnfc_design_mark="PL-1",
        rnfc_assembly_mark="AS-01",
        rnfc_element_mark="E-001",
        alpha=1.0,
    )

    holes = [
        Hole(
            xmin_mm=900,
            ymin_mm=300,
            xmax_mm=1300,
            ymax_mm=900,
            width_mm=400,
            height_mm=600,
        )
    ]

    axes = [
        AxisLine(name="X1", coord_mm=0.0, direction="X"),
        AxisLine(name="X2", coord_mm=3000.0, direction="X"),
        AxisLine(name="X3", coord_mm=6000.0, direction="X"),
        AxisLine(name="Y1", coord_mm=0.0, direction="Y"),
        AxisLine(name="Y2", coord_mm=3000.0, direction="Y"),
        AxisLine(name="Y3", coord_mm=6000.0, direction="Y"),
    ]

    cost_model = CostModel(
        steel_price_per_kg=95.0,
        complexity_penalty_per_point=120.0,
        t_min_2=20.0,
        t_2_3=40.0,
        t_3_4=70.0,
        t_4_max=110.0,
    )

    slider_cfg = SliderConfig(
        edge_exact_medium=0.34,
        edge_medium_coarse=0.67,
    )

    max_d = engine.max_additional_diameter_from_covers(slab)

    # Demo slider positions from min to max
    slider_values = [0.0, 0.25, 0.5, 0.75, 1.0]

    print("=" * 100)
    print("SLIDER DEMO: AUTO-RELAYOUT + COMPLEXITY / ECONOMY")
    print("=" * 100)
    for s in slider_values:
        result = engine.evaluate_slider_and_relayout(
            slider_value=s,
            slider_cfg=slider_cfg,
            area_grid=area_grid,
            direction="X",
            max_diameter_mm=max_d,
            slab=slab,
            rnfc=rnfc,
            bar_step_perp_mm=200,
            origin_x_mm=0.0,
            origin_y_mm=0.0,
            cost_model=cost_model,
            holes=holes,
            slab_bbox=(0, 0, 6000, 6000),
            current_row=1,
            row_diameters_mm={1: 16, 2: 12, 3: 16, 4: 12},
            axis_lines=axes,
            auto_snap_to_axis_10mm=True,
        )

        print(
            f"slider={result.slider_value:.2f} ({result.slider_label}) | "
            f"detail={result.selected_detail_level} | "
            f"zones={len(result.zones)} | "
            f"complexity={result.complexity_score:.1f} ({result.complexity_scale}) | "
            f"economy={result.economic_score:.1f}/100 | "
            f"steel_cost={result.steel_cost:.1f} | total_cost={result.total_cost:.1f}"
        )

    print("=" * 100)


if __name__ == "__main__":
    main()

