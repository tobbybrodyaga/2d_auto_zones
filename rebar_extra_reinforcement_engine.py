from __future__ import annotations

from dataclasses import dataclass, field
from math import ceil, pi
from typing import Dict, List, Literal, Optional, Tuple

Direction = Literal["X", "Y"]  # long axis of bars
DetailLevel = Literal["exact", "medium", "coarse"]


class RebarExtraReinforcementEngine:
    """
    Engine that applies your rule set to produce "zone instances" with parameters
    ready to drive Revit family placement.

    Notes / assumptions (explicit to avoid silent engineering drift):
    1) Mosaic grid value is treated as required steel area for the cell (mm^2).
       We convert it to an equivalent diameter by comparing with a single bar area.
       If your mosaic value is already "equivalent diameter" or "bar count", adjust
       area_to_diameter_mapping().
    2) Output geometry is abstract (start/end edges + width). Revit implementation
       should convert it to actual instance transforms.
    """

    # 30 мм не используем (как вы указали)
    ALLOWED_DIAMETERS_MM: Tuple[int, ...] = (8, 10, 12, 16, 20, 22, 25, 28, 32, 36)
    # SUM-3 family allowed lengths ("Длинна")
    SUM3_FAMILY_LENGTHS_MM: Tuple[int, ...] = (
        1460,
        1950,
        2340,
        2900,
        3900,
        4680,
        5850,
        7800,
        8800,
        11700,  # max
    )

    # Table 2: anchoring length (анкеровка) for A500, rounded up to 10.
    # Values are taken from user message. Units: mm.
    ANCHORAGE_LEN_MM: Dict[str, Dict[int, int]] = {
        "B15": {8: 470, 10: 580, 12: 700, 16: 930, 20: 1160, 22: 1280, 25: 1450, 28: 1630, 32: 1860, 36: 2320},
        "B20": {8: 390, 10: 490, 12: 580, 16: 780, 20: 970, 22: 1070, 25: 1210, 28: 1360, 32: 1550, 36: 1940},
        "B25": {8: 340, 10: 420, 12: 500, 16: 670, 20: 830, 22: 920, 25: 1040, 28: 1160, 32: 1330, 36: 1660},
        "B30": {8: 310, 10: 380, 12: 460, 16: 610, 20: 760, 22: 840, 25: 950, 28: 1060, 32: 1220, 36: 1520},
        "B35": {8: 270, 10: 340, 12: 410, 16: 540, 20: 670, 22: 740, 25: 840, 28: 940, 32: 1080, 36: 1340},
        "B40": {8: 250, 10: 320, 12: 380, 16: 500, 20: 630, 22: 690, 25: 780, 28: 870, 32: 1000, 36: 1250},
    }

    # Table 8: overlap length ("длинна перехлеста") for A500, rounded up to 50.
    # Values are taken from user message. Units: mm.
    LAP_LEN_MM: Dict[str, Dict[int, int]] = {
        "B15": {8: 930, 10: 1160, 12: 1400, 16: 1860, 20: 2320, 22: 2560, 25: 2900, 28: 3250, 32: 3720, 36: 4640},
        "B20": {8: 780, 10: 970, 12: 1160, 16: 1550, 20: 1940, 22: 2130, 25: 2420, 28: 2710, 32: 3100, 36: 3870},
        "B25": {8: 670, 10: 830, 12: 1000, 16: 1330, 20: 1660, 22: 1830, 25: 2080, 28: 2320, 32: 2660, 36: 3320},
        "B30": {8: 610, 10: 760, 12: 910, 16: 1220, 20: 1520, 22: 1670, 25: 1900, 28: 2120, 32: 2430, 36: 3030},
        "B35": {8: 540, 10: 670, 12: 810, 16: 1080, 20: 1340, 22: 1480, 25: 1680, 28: 1880, 32: 2150, 36: 2680},
        "B40": {8: 500, 10: 630, 12: 750, 16: 1000, 20: 1250, 22: 1370, 25: 1560, 28: 1740, 32: 1990, 36: 2490},
    }

    # Minimal mandrel diameter for A500 bending (rule for choosing G/P).
    # Units: mm.
    MIN_MANDREL_DIAM_MM: Dict[int, int] = {
        8: 40,
        10: 50,
        12: 60,
        16: 80,
        20: 160,
        22: 180,
        25: 200,
        28: 225,
        32: 260,
        36: 290,
    }

    STRAIGHT_FAMILY = "SUM-30-Зона дополнительного армирования.rfa"
    L_FAMILY = "SUM-31-Зона дополнительного армирования Г.rfa"
    P_EQUAL_FAMILY = "SUM-32-Зона дополнительного армирования П-образная равнополочная.rfa"
    P_DIFF_FAMILY = "SUM-33-Зона дополнительного армирования П-образная разнополочная.rfa"
    BENT_STICK_FAMILY = "SUM-34-Зона дополнительного армирования Гнутый стержень.rfa"

    def __init__(self, grid_spacing_mm: int = 300):
        self.grid_spacing_mm = grid_spacing_mm

    @staticmethod
    def ceil_to_step(value_mm: float, step_mm: int) -> int:
        if step_mm <= 0:
            raise ValueError("step_mm must be positive")
        return int(ceil(value_mm / step_mm) * step_mm)

    @classmethod
    def bar_area_mm2(cls, diameter_mm: int) -> float:
        d = float(diameter_mm)
        return pi * d * d / 4.0

    def compute_anchorage_len_mm(self, concrete_class: str, diameter_mm: int) -> int:
        d = self._nearest_supported_diameter(diameter_mm, self.ANCHORAGE_LEN_MM[concrete_class].keys())
        base = self.ANCHORAGE_LEN_MM[concrete_class][d]
        # rule: rounded up to 10
        return self.ceil_to_step(base, 10)

    def compute_lap_len_mm(self, concrete_class: str, diameter_mm: int) -> int:
        d = self._nearest_supported_diameter(diameter_mm, self.LAP_LEN_MM[concrete_class].keys())
        base = self.LAP_LEN_MM[concrete_class][d]
        # rule: overlap rounded up to 50
        return self.ceil_to_step(base, 50)

    @staticmethod
    def _nearest_supported_diameter(requested: int, available_diameters) -> int:
        """
        If a diameter was requested but not present in a table (e.g. 30),
        pick the nearest supported diameter <= requested.
        """
        available = sorted(int(d) for d in available_diameters)
        if not available:
            raise ValueError("No available diameters in table.")
        if requested in available:
            return requested
        le = [d for d in available if d <= requested]
        return max(le) if le else min(available)

    def max_additional_diameter_from_covers(
        self,
        slab: SlabGeometry,
    ) -> int:
        """
        Rule 3 in your spec: protective layers define max diameter.

        Because your exact code/covering geometry isn't fully specified,
        we implement a conservative geometric constraint:
        - additional bar is placed with at least min_clear_inside_mm clearance
          from the nearer face to bar surface.
        """
        min_cover_mm = min(slab.cover_top_mm, slab.cover_bottom_mm)
        max_d_by_cover = 2.0 * max(0.0, min_cover_mm - slab.min_clear_inside_mm)
        # Choose the maximum allowed diameter <= that cap.
        allowed = [d for d in self.ALLOWED_DIAMETERS_MM if d <= max_d_by_cover]
        return max(allowed) if allowed else min(self.ALLOWED_DIAMETERS_MM)

    def compute_vertical_leg_available_mm(
        self,
        slab: SlabGeometry,
        *,
        opposite_edge_row_diameter_mm: int,
    ) -> float:
        """
        Rule (your message): vertical anchorage leg is approximated as:
            thickness - (cover_bottom + cover_top + diameter of relevant row)

        Here "relevant row" means opposite extreme row by your rule:
        - current row in (1,3) -> subtract row 4 diameter
        - current row in (2,4) -> subtract row 1 diameter
        """
        return float(
            slab.thickness_mm
            - (slab.cover_bottom_mm + slab.cover_top_mm + opposite_edge_row_diameter_mm)
        )

    @staticmethod
    def opposite_edge_row_for_current_row(current_row: int) -> int:
        """
        Mapping from your last clarification:
        - row 1 bends toward 3 -> subtract row 4
        - row 3 bends toward 1 -> subtract row 4
        - row 2 bends toward 4 -> subtract row 1
        - row 4 bends toward 2 -> subtract row 1
        """
        if current_row in (1, 3):
            return 4
        if current_row in (2, 4):
            return 1
        raise ValueError("current_row must be one of 1,2,3,4.")

    def choose_bent_family_by_bending_criteria(
        self,
        slab: SlabGeometry,
        diameter_mm: int,
        zone_direction: Direction,
        *,
        current_row: int,
        row_diameters_mm: Optional[Dict[int, int]] = None,
    ) -> str:
        """
        Select SUM-31/32/33/34 based on mandrel diameter requirement:
        - Г-shape: vertical_available >= D_mandrel
        - П-shape: vertical_available >= 2 * D_mandrel (two bends)
        Fallback: SUM-34 if neither criterion passes.
        """
        d = self._nearest_supported_diameter(diameter_mm, self.MIN_MANDREL_DIAM_MM.keys())

        # By default rows use zone diameter if row-specific diameters are not provided.
        if row_diameters_mm is None:
            row_diameters_mm = {1: d, 2: d, 3: d, 4: d}

        opposite_row = self.opposite_edge_row_for_current_row(current_row)
        diameter_adjacent_row = int(row_diameters_mm.get(opposite_row, d))
        vertical_available = self.compute_vertical_leg_available_mm(
            slab,
            opposite_edge_row_diameter_mm=diameter_adjacent_row,
        )
        d_mandrel = float(self.MIN_MANDREL_DIAM_MM[d])

        # If vertical leg is very large, P-shape is also feasible by mandrel criterion.
        if vertical_available >= 2.0 * d_mandrel:
            return self.P_EQUAL_FAMILY  # SUM-32
        if vertical_available >= d_mandrel:
            return self.L_FAMILY  # SUM-31 (G-shape)
        return self.BENT_STICK_FAMILY  # SUM-34 fallback

    def area_to_equivalent_diameter_mm(
        self,
        required_area_mm2: float,
        max_diameter_mm: int,
        area_assumes_single_bar_per_cell: bool = True,
    ) -> int:
        """
        Mapping from mosaic cell "area of additional reinforcement" to Ø.
        Default: required_area_mm2 is "area that one bar should provide".
        """
        if required_area_mm2 <= 0:
            return 0  # no need
        max_d = min(max_diameter_mm, max(self.ALLOWED_DIAMETERS_MM))
        candidates = [d for d in self.ALLOWED_DIAMETERS_MM if d <= max_d]
        if not candidates:
            return 0
        # Equivalent: pick minimal diameter whose single-bar area >= required.
        for d in candidates:
            if self.bar_area_mm2(d) >= required_area_mm2:
                return d
        return max(candidates)

    @staticmethod
    def smooth_single_spike(
        area_grid: List[List[float]],
        spike_ratio: float = 1.8,
    ) -> List[List[float]]:
        """
        Rule 5.1: if 1 node has a big spike, average with neighbors.

        Implementation: compare cell against mean of 4-neighbors (up/down/left/right)
        and replace if value > spike_ratio * neighbor_mean.
        """
        ny = len(area_grid)
        nx = len(area_grid[0]) if ny else 0
        if ny == 0 or nx == 0:
            return area_grid

        out = [row[:] for row in area_grid]
        for iy in range(1, ny - 1):
            for ix in range(1, nx - 1):
                v = area_grid[iy][ix]
                if v <= 0:
                    continue
                neighbors = [
                    area_grid[iy - 1][ix],
                    area_grid[iy + 1][ix],
                    area_grid[iy][ix - 1],
                    area_grid[iy][ix + 1],
                ]
                neighbor_mean = sum(neighbors) / 4.0
                if neighbor_mean <= 0:
                    continue
                if v > spike_ratio * neighbor_mean:
                    out[iy][ix] = neighbor_mean
        return out

    def _local_maxima_mask(self, area_grid: List[List[float]]) -> List[List[bool]]:
        ny = len(area_grid)
        nx = len(area_grid[0]) if ny else 0
        mask = [[False] * nx for _ in range(ny)]
        for iy in range(1, ny - 1):
            for ix in range(1, nx - 1):
                v = area_grid[iy][ix]
                if v <= 0:
                    continue
                if (
                    v >= area_grid[iy - 1][ix]
                    and v >= area_grid[iy + 1][ix]
                    and v >= area_grid[iy][ix - 1]
                    and v >= area_grid[iy][ix + 1]
                ):
                    mask[iy][ix] = True
        return mask

    def _detail_threshold_ratio(self, level: DetailLevel) -> float:
        # Lower ratio => bigger zones in aggregated level.
        if level in ("exact", "точный"):
            return 0.95
        if level in ("medium", "средний"):
            return 0.70
        return 0.40

    def _pick_family_length(self, required_len_mm: float) -> int:
        for L in self.SUM3_FAMILY_LENGTHS_MM:
            if L >= required_len_mm:
                return L
        # if required length exceeds max: return max and handle segmentation separately
        return self.SUM3_FAMILY_LENGTHS_MM[-1]

    def _split_by_max_length(
        self,
        long_start_mm: float,
        long_end_mm: float,
        diameter_mm: int,
        concrete_class: str,
        alpha: float,
    ) -> List[Tuple[float, float]]:
        """
        Rule 5.5: if not enough length, cover with multiple SUM-3 families.

        We model overlap between consecutive segments as:
        overlap_between_segments = 2 * alpha * lap_len_single
        (double overlap).

        Returns list of (seg_start, seg_end) edges.
        """
        maxL = self.SUM3_FAMILY_LENGTHS_MM[-1]
        if long_end_mm - long_start_mm <= maxL:
            return [(long_start_mm, long_end_mm)]

        lap_len = self.compute_lap_len_mm(concrete_class, diameter_mm)
        overlap_between = 2.0 * float(alpha) * float(lap_len)
        if overlap_between >= maxL:
            # Safety: avoid negative advance
            overlap_between = maxL * 0.5

        step_advance = maxL - overlap_between
        if step_advance <= 0:
            step_advance = maxL * 0.5

        segs: List[Tuple[float, float]] = []
        cur_start = long_start_mm
        while cur_start < long_end_mm - 1e-6:
            cur_end = min(cur_start + maxL, long_end_mm)
            segs.append((cur_start, cur_end))
            if cur_end >= long_end_mm - 1e-6:
                break
            cur_start = cur_start + step_advance

        # Normalize: choose family discrete lengths if desired later in Revit;
        # for now, we keep exact edges derived from long_end_mm.
        return segs

    @staticmethod
    def steel_unit_weight_kg_per_m(diameter_mm: int) -> float:
        """
        Standard approximation for rebar mass:
            q [kg/m] = 0.006165 * d^2
        """
        d = float(diameter_mm)
        return 0.006165 * d * d

    def estimate_zone_length_and_mass(self, zone: ZoneInstance) -> Tuple[float, float]:
        """
        Returns:
        - total bar length in mm for this zone
        - total mass in kg for this zone
        """
        zone_len_mm = max(0.0, float(zone.long_end_mm - zone.long_start_mm))
        total_len_mm = zone_len_mm * max(1, int(zone.bar_count))
        total_len_m = total_len_mm / 1000.0
        mass_kg = total_len_m * self.steel_unit_weight_kg_per_m(zone.diameter_mm)
        return total_len_mm, mass_kg

    @staticmethod
    def _nearest_axis(coord_mm: float, axis_lines: List[AxisLine]) -> AxisLine:
        if not axis_lines:
            raise ValueError("axis_lines cannot be empty")
        return min(axis_lines, key=lambda a: abs(coord_mm - a.coord_mm))

    @staticmethod
    def _is_multiple(value_mm: float, step_mm: float, tol_mm: float = 1e-6) -> bool:
        if step_mm <= 0:
            raise ValueError("step_mm must be positive")
        rem = abs(value_mm) % step_mm
        return rem <= tol_mm or abs(step_mm - rem) <= tol_mm

    @staticmethod
    def _round_to_multiple(value_mm: float, step_mm: float) -> float:
        return round(value_mm / step_mm) * step_mm

    def check_zone_axis_binding_and_10mm_modularity(
        self,
        zones: List[ZoneInstance],
        axis_lines: List[AxisLine],
        *,
        distance_step_mm: float = 10.0,
        tol_mm: float = 1e-6,
    ) -> List[ZoneAxisCheckResult]:
        """
        Validate:
        1) Zone rectangle edges are bound to nearest structural axes.
        2) Distances from all 4 edges (x_min/x_max/y_min/y_max) to nearest axes
           are multiples of 10 mm.
        """
        results: List[ZoneAxisCheckResult] = []
        for i, z in enumerate(zones):
            axes_x = [a for a in axis_lines if a.direction == "X"]
            axes_y = [a for a in axis_lines if a.direction == "Y"]
            if not axes_x or not axes_y:
                raise ValueError("Both X and Y axis sets are required.")

            # Fallback to long edges if rectangle not yet set.
            if z.x_min_mm is None or z.x_max_mm is None or z.y_min_mm is None or z.y_max_mm is None:
                if z.direction == "X":
                    x_min = float(z.long_start_mm)
                    x_max = float(z.long_end_mm)
                    y_center = 0.0
                    y_min = y_center - float(z.width_mm) / 2.0
                    y_max = y_center + float(z.width_mm) / 2.0
                else:
                    y_min = float(z.long_start_mm)
                    y_max = float(z.long_end_mm)
                    x_center = 0.0
                    x_min = x_center - float(z.width_mm) / 2.0
                    x_max = x_center + float(z.width_mm) / 2.0
            else:
                x_min = float(z.x_min_mm)
                x_max = float(z.x_max_mm)
                y_min = float(z.y_min_mm)
                y_max = float(z.y_max_mm)

            ax_x_min = self._nearest_axis(x_min, axes_x)
            ax_x_max = self._nearest_axis(x_max, axes_x)
            ax_y_min = self._nearest_axis(y_min, axes_y)
            ax_y_max = self._nearest_axis(y_max, axes_y)

            dx_min = abs(x_min - ax_x_min.coord_mm)
            dx_max = abs(x_max - ax_x_max.coord_mm)
            dy_min = abs(y_min - ax_y_min.coord_mm)
            dy_max = abs(y_max - ax_y_max.coord_mm)

            x_min_ok = self._is_multiple(dx_min, distance_step_mm, tol_mm=tol_mm)
            x_max_ok = self._is_multiple(dx_max, distance_step_mm, tol_mm=tol_mm)
            y_min_ok = self._is_multiple(dy_min, distance_step_mm, tol_mm=tol_mm)
            y_max_ok = self._is_multiple(dy_max, distance_step_mm, tol_mm=tol_mm)

            results.append(
                ZoneAxisCheckResult(
                    zone_index=i,
                    direction=z.direction,
                    nearest_axis_x_min=ax_x_min.name,
                    nearest_axis_x_max=ax_x_max.name,
                    nearest_axis_y_min=ax_y_min.name,
                    nearest_axis_y_max=ax_y_max.name,
                    dist_x_min_mm=dx_min,
                    dist_x_max_mm=dx_max,
                    dist_y_min_mm=dy_min,
                    dist_y_max_mm=dy_max,
                    x_min_is_multiple_of_10=x_min_ok,
                    x_max_is_multiple_of_10=x_max_ok,
                    y_min_is_multiple_of_10=y_min_ok,
                    y_max_is_multiple_of_10=y_max_ok,
                    is_ok=(x_min_ok and x_max_ok and y_min_ok and y_max_ok),
                )
            )
        return results

    def snap_zone_edges_to_axis_10mm(
        self,
        zones: List[ZoneInstance],
        axis_lines: List[AxisLine],
        *,
        distance_step_mm: float = 10.0,
    ) -> List[ZoneInstance]:
        """
        Auto-correct zone edge positions for full rectangle:
        For each edge x_min/x_max/y_min/y_max, keep nearest axis,
        then round distance-to-axis to nearest 10 mm.
        """
        snapped: List[ZoneInstance] = []
        for z in zones:
            axes_x = [a for a in axis_lines if a.direction == "X"]
            axes_y = [a for a in axis_lines if a.direction == "Y"]
            if not axes_x or not axes_y:
                raise ValueError("Both X and Y axis sets are required.")

            # Fallback if rectangle is not set.
            if z.x_min_mm is None or z.x_max_mm is None or z.y_min_mm is None or z.y_max_mm is None:
                if z.direction == "X":
                    z.x_min_mm = float(z.long_start_mm)  # type: ignore[misc]
                    z.x_max_mm = float(z.long_end_mm)  # type: ignore[misc]
                    z.y_min_mm = -float(z.width_mm) / 2.0  # type: ignore[misc]
                    z.y_max_mm = float(z.width_mm) / 2.0  # type: ignore[misc]
                else:
                    z.y_min_mm = float(z.long_start_mm)  # type: ignore[misc]
                    z.y_max_mm = float(z.long_end_mm)  # type: ignore[misc]
                    z.x_min_mm = -float(z.width_mm) / 2.0  # type: ignore[misc]
                    z.x_max_mm = float(z.width_mm) / 2.0  # type: ignore[misc]

            ax_x_min = self._nearest_axis(float(z.x_min_mm), axes_x)
            ax_x_max = self._nearest_axis(float(z.x_max_mm), axes_x)
            ax_y_min = self._nearest_axis(float(z.y_min_mm), axes_y)
            ax_y_max = self._nearest_axis(float(z.y_max_mm), axes_y)

            z.x_min_mm = float(ax_x_min.coord_mm + self._round_to_multiple(float(z.x_min_mm) - ax_x_min.coord_mm, distance_step_mm))  # type: ignore[misc]
            z.x_max_mm = float(ax_x_max.coord_mm + self._round_to_multiple(float(z.x_max_mm) - ax_x_max.coord_mm, distance_step_mm))  # type: ignore[misc]
            z.y_min_mm = float(ax_y_min.coord_mm + self._round_to_multiple(float(z.y_min_mm) - ax_y_min.coord_mm, distance_step_mm))  # type: ignore[misc]
            z.y_max_mm = float(ax_y_max.coord_mm + self._round_to_multiple(float(z.y_max_mm) - ax_y_max.coord_mm, distance_step_mm))  # type: ignore[misc]

            # Keep long_start/long_end synchronized.
            if z.direction == "X":
                z.long_start_mm = float(z.x_min_mm)  # type: ignore[misc]
                z.long_end_mm = float(z.x_max_mm)  # type: ignore[misc]
            else:
                z.long_start_mm = float(z.y_min_mm)  # type: ignore[misc]
                z.long_end_mm = float(z.y_max_mm)  # type: ignore[misc]
            snapped.append(z)

        return snapped

    def summarize_level_cost(self, zones: List[ZoneInstance], detail_level: DetailLevel, cost_model: CostModel) -> LevelCostSummary:
        total_len_mm = 0.0
        total_mass_kg = 0.0
        bent_count = 0
        overlap_joint_count = 0

        for z in zones:
            l_mm, m_kg = self.estimate_zone_length_and_mass(z)
            total_len_mm += l_mm
            total_mass_kg += m_kg

            if z.family_name in (self.L_FAMILY, self.P_EQUAL_FAMILY, self.P_DIFF_FAMILY, self.BENT_STICK_FAMILY):
                bent_count += 1

            # Approximate overlap joints by "how many max-length bars fit per bar line minus 1"
            seg_len = max(0.0, float(z.long_end_mm - z.long_start_mm))
            if seg_len > self.SUM3_FAMILY_LENGTHS_MM[-1]:
                joints_per_line = int(ceil(seg_len / self.SUM3_FAMILY_LENGTHS_MM[-1])) - 1
                overlap_joint_count += max(0, joints_per_line) * max(1, int(z.bar_count))

        steel_cost = total_mass_kg * float(cost_model.steel_price_per_kg)
        complexity_score = (
            float(cost_model.zone_count_weight) * float(len(zones))
            + float(cost_model.overlap_joint_weight) * float(overlap_joint_count)
            + float(cost_model.bent_zone_weight) * float(bent_count)
        )
        complexity_scale = self.complexity_scale_from_score(complexity_score, cost_model)
        complexity_penalty_cost = complexity_score * float(cost_model.complexity_penalty_per_point)
        total_cost = steel_cost + complexity_penalty_cost

        return LevelCostSummary(
            detail_level=detail_level,
            zone_count=len(zones),
            bent_zone_count=bent_count,
            overlap_joint_count=overlap_joint_count,
            total_bar_length_mm=total_len_mm,
            total_mass_kg=total_mass_kg,
            steel_cost=steel_cost,
            complexity_score=complexity_score,
            complexity_scale=complexity_scale,
            complexity_penalty_cost=complexity_penalty_cost,
            total_cost=total_cost,
        )

    @staticmethod
    def complexity_scale_from_score(
        score: float,
        cost_model: CostModel,
    ) -> Literal["min", "2", "3", "4", "max"]:
        """
        Fixed 5-level complexity scale requested by user:
            min -> 2 -> 3 -> 4 -> max
        Thresholds are configurable in CostModel.
        """
        if score < float(cost_model.t_min_2):
            return "min"
        if score < float(cost_model.t_2_3):
            return "2"
        if score < float(cost_model.t_3_4):
            return "3"
        if score < float(cost_model.t_4_max):
            return "4"
        return "max"

    def extract_straight_zones(
        self,
        *,
        area_grid: List[List[float]],  # ny x nx
        direction: Direction,
        detail_level: DetailLevel,
        max_diameter_mm: int,
        slab: SlabGeometry,
        rnfc: RNFAttributes,
        bar_step_perp_mm: int,
        # Coordinate mapping: top-left of cell (0,0) to plan mm
        origin_x_mm: float,
        origin_y_mm: float,
        # If mosaic cell (ix,iy) corresponds to cell square [ix*300,(ix+1)*300] etc.
        cell_size_mm: Optional[int] = None,
        # Apply rule 5.1 spike smoothing before peak detection.
        smooth_spikes: bool = True,
        spike_ratio: float = 1.8,
    ) -> List[ZoneInstance]:
        """
        Rules implemented:
        - 1) direction (long axis X/Y)
        - 4) detail level => threshold ratio for growth
        - 5.1 peaks cover + spike smoothing (call smooth first outside or inside)
        - 5.2 pick length from SUM-3 list
        - 5.3 width multiple of bar_step_perp_mm (via bar_count computation)
        - 5.4 neighbor offsets: handled implicitly by greedy peak processing.
        - 5.5 segmentation by max length and overlap.
        """
        if cell_size_mm is None:
            cell_size_mm = self.grid_spacing_mm

        ny = len(area_grid)
        nx = len(area_grid[0]) if ny else 0
        if ny == 0 or nx == 0:
            return []

        if smooth_spikes:
            area_grid = self.smooth_single_spike(area_grid, spike_ratio=spike_ratio)

        threshold_ratio = self._detail_threshold_ratio(detail_level)
        maxima = self._local_maxima_mask(area_grid)

        # Sort all peaks by value descending.
        peaks: List[Tuple[float, int, int]] = []
        for iy in range(ny):
            for ix in range(nx):
                if maxima[iy][ix]:
                    peaks.append((area_grid[iy][ix], ix, iy))
        peaks.sort(reverse=True, key=lambda t: t[0])

        # Track which cells are already assigned as "covered core" (greedy).
        assigned = [[False] * nx for _ in range(ny)]

        zones: List[ZoneInstance] = []

        for v_peak, ix0, iy0 in peaks:
            if v_peak <= 0:
                continue
            if assigned[iy0][ix0]:
                continue

            d_zone = self.area_to_equivalent_diameter_mm(v_peak, max_diameter_mm)
            if d_zone == 0:
                continue

            A_threshold = threshold_ratio * v_peak

            # Expand along long axis and perpendicular.
            # If direction == X: long axis changes ix, perpendicular changes iy.
            if direction == "X":
                # long expansion along ix
                i_left = ix0
                while i_left - 1 >= 0 and area_grid[iy0][i_left - 1] >= A_threshold:
                    i_left -= 1
                i_right = ix0
                while i_right + 1 < nx and area_grid[iy0][i_right + 1] >= A_threshold:
                    i_right += 1

                # width expansion along iy
                j_down = iy0
                while j_down - 1 >= 0 and area_grid[j_down - 1][ix0] >= A_threshold:
                    j_down -= 1
                j_up = iy0
                while j_up + 1 < ny and area_grid[j_up + 1][ix0] >= A_threshold:
                    j_up += 1
            else:
                # direction == Y: long expansion along iy, perpendicular changes ix
                i_left = ix0
                while i_left - 1 >= 0 and area_grid[iy0][i_left - 1] >= A_threshold:
                    i_left -= 1
                i_right = ix0
                while i_right + 1 < nx and area_grid[iy0][i_right + 1] >= A_threshold:
                    i_right += 1

                j_down = iy0
                while j_down - 1 >= 0 and area_grid[j_down - 1][ix0] >= A_threshold:
                    j_down -= 1
                j_up = iy0
                while j_up + 1 < ny and area_grid[j_up + 1][ix0] >= A_threshold:
                    j_up += 1

            # Mark assigned cells in the grown rectangle core.
            for iy in range(j_down, j_up + 1):
                for ix in range(i_left, i_right + 1):
                    assigned[iy][ix] = True

            # Bounding box edges in mm.
            # Cell ix spans [origin_x + ix*cell, origin_x + (ix+1)*cell]
            x0_edge = origin_x_mm + i_left * cell_size_mm
            x1_edge = origin_x_mm + (i_right + 1) * cell_size_mm
            y0_edge = origin_y_mm + j_down * cell_size_mm
            y1_edge = origin_y_mm + (j_up + 1) * cell_size_mm

            if direction == "X":
                long_start_edge = x0_edge
                long_end_edge = x1_edge
            else:
                long_start_edge = y0_edge
                long_end_edge = y1_edge

            # Rule 5.1: zone must extend beyond peak spot by anchorage length.
            anc_len = self.compute_anchorage_len_mm(slab.concrete_class, d_zone)
            long_start_total = long_start_edge - float(anc_len)
            long_end_total = long_end_edge + float(anc_len)

            required_len = long_end_total - long_start_total
            _picked_L = self._pick_family_length(required_len)  # informational

            # Width across perpendicular:
            # use total required area within core rectangle to compute bar_count.
            total_area = 0.0
            for iy in range(j_down, j_up + 1):
                for ix in range(i_left, i_right + 1):
                    total_area += float(area_grid[iy][ix])

            bar_area = self.bar_area_mm2(d_zone)
            bars_needed = int(ceil(total_area / max(1e-9, bar_area)))
            bars_needed = max(bars_needed, 1)

            # Ensure width covers at least the perpendicular span.
            span_perp_mm = (j_up - j_down + 1) * cell_size_mm if direction == "X" else (i_right - i_left + 1) * cell_size_mm
            min_bars_for_span = int(ceil((span_perp_mm / float(bar_step_perp_mm))))
            # width attribute is (count-1)*step, so with count bars width spans (count-1)*step
            # we require (count-1)*step >= span_perp_mm - small tolerance
            min_count = max(1, int(ceil(span_perp_mm / float(bar_step_perp_mm))) + 1)
            bars_needed = max(bars_needed, min_count)

            width_mm = float((bars_needed - 1) * bar_step_perp_mm)

            # Rule 5.2: family "Длинна" taken from SUM-3 list (discrete).
            # We implement exact placement edges and Revit will map to discrete family length
            # (or we can round each segment length here).
            segments = self._split_by_max_length(
                long_start_total,
                long_end_total,
                diameter_mm=d_zone,
                concrete_class=slab.concrete_class,
                alpha=rnfc.alpha,
            )

            for seg_start, seg_end in segments:
                seg_len = seg_end - seg_start
                family_len = self._pick_family_length(seg_len)
                # Expand segment to match picked family length (ensures full coverage).
                seg_center = (seg_start + seg_end) / 2.0
                seg_start_adj = seg_center - family_len / 2.0
                seg_end_adj = seg_center + family_len / 2.0

                # Build full rectangular footprint.
                core_x_center = (x0_edge + x1_edge) / 2.0
                core_y_center = (y0_edge + y1_edge) / 2.0
                half_w = width_mm / 2.0
                if direction == "X":
                    x_min_mm = seg_start_adj
                    x_max_mm = seg_end_adj
                    y_min_mm = core_y_center - half_w
                    y_max_mm = core_y_center + half_w
                else:
                    y_min_mm = seg_start_adj
                    y_max_mm = seg_end_adj
                    x_min_mm = core_x_center - half_w
                    x_max_mm = core_x_center + half_w

                zones.append(
                    ZoneInstance(
                        family_name=self.STRAIGHT_FAMILY,
                        direction=direction,
                        long_start_mm=seg_start_adj,
                        long_end_mm=seg_end_adj,
                        width_mm=width_mm,
                        bar_step_mm=bar_step_perp_mm,
                        bar_count=bars_needed,
                        diameter_mm=d_zone,
                        concrete_class=slab.concrete_class,
                        alpha=rnfc.alpha,
                        rnfc=rnfc,
                        include_in_schedule=True,
                        count_bars_only_for_bends=False,
                        callout_short=None,
                        callout_bar_count=bars_needed,
                        x_min_mm=x_min_mm,
                        x_max_mm=x_max_mm,
                        y_min_mm=y_min_mm,
                        y_max_mm=y_max_mm,
                    )
                )

        return zones

    def apply_hole_rule_for_straight_zones(
        self,
        zones: List[ZoneInstance],
        holes: List[Hole],
        slab: SlabGeometry,
        *,
        slab_bbox: Tuple[float, float, float, float],  # xmin,ymin,xmax,ymax
        direction: Direction,
        current_row: int,
        row_diameters_mm: Optional[Dict[int, int]] = None,
        hole_ignore_threshold_mm: int = 200,
        offset_to_hole_edge_mm: int = 50,
    ) -> List[ZoneInstance]:
        """
        Partial implementation of rule 5.6 (holes):
        - if hole intersects a straight zone:
          - ignore holes <= 200 in perpendicular direction
          - otherwise replace by a placeholder bent family decision
            (SUM-34 or edge families) to be refined with exact geometry.

        We do NOT modify exact geometry yet; instead we:
        - mark intersected zones as needing bent detailing by switching family_name
          according to the mandrel criteria (SUM-31/32/34).
        """
        xmin_s, ymin_s, xmax_s, ymax_s = slab_bbox
        # No sophisticated geometry operations: this is a first deterministic pass.
        out: List[ZoneInstance] = []

        for z in zones:
            # Compute axis-aligned rectangle of the straight zone in plan:
            # - For direction X: long axis is X (x range), width spans Y.
            # We don't have the perpendicular origin for the zone, so we approximate width
            # centered at plan coordinate 0. This method must be connected to actual placement
            # coordinates in the Revit layer.
            # Therefore: we only apply "ignore <=200" and set family to bent for big holes
            # based on abstract length overlap (caller should refine).

            intersects_big_hole = False
            for h in holes:
                # Determine perpendicular dimension to compare with 200 rule.
                perp_dim = h.height_mm if direction == "X" else h.width_mm
                if perp_dim <= hole_ignore_threshold_mm:
                    continue
                # Intersection test is conservative placeholder:
                # if hole rectangle is within slab bbox and overlaps in at least one axis,
                # treat as intersecting.
                # Caller should refine with actual zone placement coordinates.
                if not (h.xmax_mm <= xmin_s or h.xmin_mm >= xmax_s or h.ymax_mm <= ymin_s or h.ymin_mm >= ymax_s):
                    intersects_big_hole = True
                    break

            if intersects_big_hole:
                # Choose bent family using mandrel constraints.
                # Revit layer will later translate this to real hook/P geometry.
                z.family_name = self.choose_bent_family_by_bending_criteria(
                    slab,
                    z.diameter_mm,
                    zone_direction=direction,
                    current_row=current_row,
                    row_diameters_mm=row_diameters_mm,
                )
                z.include_in_schedule = False  # bent parts are counted by "подсчет стержней"
                z.count_bars_only_for_bends = True

                if z.family_name == self.L_FAMILY:
                    z.callout_short = "Г"
                elif z.family_name in (self.P_EQUAL_FAMILY, self.P_DIFF_FAMILY):
                    z.callout_short = "П"
                else:
                    z.callout_short = "ГС"
            out.append(z)

        return out

    def compare_detail_levels_cost(
        self,
        *,
        area_grid: List[List[float]],
        direction: Direction,
        max_diameter_mm: int,
        slab: SlabGeometry,
        rnfc: RNFAttributes,
        bar_step_perp_mm: int,
        origin_x_mm: float,
        origin_y_mm: float,
        cost_model: CostModel,
        # optional post-processing by holes
        holes: Optional[List[Hole]] = None,
        slab_bbox: Optional[Tuple[float, float, float, float]] = None,
        current_row: Optional[int] = None,
        row_diameters_mm: Optional[Dict[int, int]] = None,
        cell_size_mm: Optional[int] = None,
        smooth_spikes: bool = True,
        spike_ratio: float = 1.8,
    ) -> List[LevelCostSummary]:
        """
        Build and compare alternatives for all detail levels:
        - exact: better steel economy, more layout complexity
        - medium: balance
        - coarse: simpler layout, usually more steel consumption
        """
        levels: Tuple[DetailLevel, ...] = ("exact", "medium", "coarse")
        summaries: List[LevelCostSummary] = []

        for lvl in levels:
            zones = self.extract_straight_zones(
                area_grid=area_grid,
                direction=direction,
                detail_level=lvl,
                max_diameter_mm=max_diameter_mm,
                slab=slab,
                rnfc=rnfc,
                bar_step_perp_mm=bar_step_perp_mm,
                origin_x_mm=origin_x_mm,
                origin_y_mm=origin_y_mm,
                cell_size_mm=cell_size_mm,
                smooth_spikes=smooth_spikes,
                spike_ratio=spike_ratio,
            )

            if holes and slab_bbox is not None and current_row is not None:
                zones = self.apply_hole_rule_for_straight_zones(
                    zones=zones,
                    holes=holes,
                    slab=slab,
                    slab_bbox=slab_bbox,
                    direction=direction,
                    current_row=current_row,
                    row_diameters_mm=row_diameters_mm,
                )

            summaries.append(self.summarize_level_cost(zones, lvl, cost_model))

        # Sort by total cost (lowest first) to simplify UI decision.
        summaries.sort(key=lambda s: s.total_cost)
        return summaries

    @staticmethod
    def _clamp01(v: float) -> float:
        return max(0.0, min(1.0, float(v)))

    def slider_value_to_detail_level(self, slider_value: float, slider_cfg: SliderConfig) -> DetailLevel:
        """
        Map continuous slider to discrete layout mode.
        """
        s = self._clamp01(slider_value)
        if s < slider_cfg.edge_exact_medium:
            return "exact"
        if s < slider_cfg.edge_medium_coarse:
            return "medium"
        return "coarse"

    def slider_value_to_label(self, slider_value: float) -> Literal["min", "2", "3", "4", "max"]:
        """
        Presentable 5-step label for UI while keeping continuous slider.
        """
        s = self._clamp01(slider_value)
        if s < 0.2:
            return "min"
        if s < 0.4:
            return "2"
        if s < 0.6:
            return "3"
        if s < 0.8:
            return "4"
        return "max"

    @staticmethod
    def economic_score_from_total_cost(total_cost: float, baseline_best_cost: float) -> float:
        """
        Economic score in [0..100], where 100 = best (lowest total cost).
        """
        base = max(1e-9, float(baseline_best_cost))
        ratio = float(total_cost) / base
        # 100 at ratio=1, decreasing smoothly.
        score = 100.0 / ratio
        return max(0.0, min(100.0, score))

    def evaluate_slider_and_relayout(
        self,
        *,
        slider_value: float,
        slider_cfg: SliderConfig,
        area_grid: List[List[float]],
        direction: Direction,
        max_diameter_mm: int,
        slab: SlabGeometry,
        rnfc: RNFAttributes,
        bar_step_perp_mm: int,
        origin_x_mm: float,
        origin_y_mm: float,
        cost_model: CostModel,
        holes: Optional[List[Hole]] = None,
        slab_bbox: Optional[Tuple[float, float, float, float]] = None,
        current_row: Optional[int] = None,
        row_diameters_mm: Optional[Dict[int, int]] = None,
        axis_lines: Optional[List[AxisLine]] = None,
        auto_snap_to_axis_10mm: bool = False,
        cell_size_mm: Optional[int] = None,
        smooth_spikes: bool = True,
        spike_ratio: float = 1.8,
    ) -> SliderEvaluationResult:
        """
        Main API for interactive slider:
        - resolves detail level by slider position
        - relayouts zones
        - computes complexity/economic metrics
        - optionally snaps zones to axis 10mm modularity
        """
        lvl = self.slider_value_to_detail_level(slider_value, slider_cfg)
        slider_label = self.slider_value_to_label(slider_value)

        zones = self.extract_straight_zones(
            area_grid=area_grid,
            direction=direction,
            detail_level=lvl,
            max_diameter_mm=max_diameter_mm,
            slab=slab,
            rnfc=rnfc,
            bar_step_perp_mm=bar_step_perp_mm,
            origin_x_mm=origin_x_mm,
            origin_y_mm=origin_y_mm,
            cell_size_mm=cell_size_mm,
            smooth_spikes=smooth_spikes,
            spike_ratio=spike_ratio,
        )

        if holes and slab_bbox is not None and current_row is not None:
            zones = self.apply_hole_rule_for_straight_zones(
                zones=zones,
                holes=holes,
                slab=slab,
                slab_bbox=slab_bbox,
                direction=direction,
                current_row=current_row,
                row_diameters_mm=row_diameters_mm,
            )

        if auto_snap_to_axis_10mm and axis_lines:
            zones = self.snap_zone_edges_to_axis_10mm(zones, axis_lines)

        # Cost for selected slider state.
        selected_summary = self.summarize_level_cost(zones, lvl, cost_model)

        # Baseline best cost among all three levels (same input).
        all_summaries = self.compare_detail_levels_cost(
            area_grid=area_grid,
            direction=direction,
            max_diameter_mm=max_diameter_mm,
            slab=slab,
            rnfc=rnfc,
            bar_step_perp_mm=bar_step_perp_mm,
            origin_x_mm=origin_x_mm,
            origin_y_mm=origin_y_mm,
            cost_model=cost_model,
            holes=holes,
            slab_bbox=slab_bbox,
            current_row=current_row,
            row_diameters_mm=row_diameters_mm,
            cell_size_mm=cell_size_mm,
            smooth_spikes=smooth_spikes,
            spike_ratio=spike_ratio,
        )
        best_total_cost = min(s.total_cost for s in all_summaries) if all_summaries else selected_summary.total_cost
        econ_score = self.economic_score_from_total_cost(selected_summary.total_cost, best_total_cost)

        return SliderEvaluationResult(
            slider_value=self._clamp01(slider_value),
            slider_label=slider_label,
            selected_detail_level=lvl,
            complexity_score=selected_summary.complexity_score,
            complexity_scale=selected_summary.complexity_scale,
            economic_score=econ_score,
            steel_cost=selected_summary.steel_cost,
            total_cost=selected_summary.total_cost,
            zones=zones,
        )
@dataclass(frozen=True)
class Hole:
    """
    Plan-space axis-aligned rectangular hole.

    width_mm/height_mm must be mapped to Revit family parameters:
    - if zone direction is X, "Отверстиве_Высота" corresponds to height_mm
    - if zone direction is Y, "Отверстие_Ширина" corresponds to width_mm

    Positions are in mm in the same coordinate system as the mosaic grid.
    """

    xmin_mm: float
    ymin_mm: float
    xmax_mm: float
    ymax_mm: float
    width_mm: float
    height_mm: float


@dataclass(frozen=True)
class AxisLine:
    """
    Structural axis line coordinate in plan.
    For current abstract engine we use scalar coordinate only.
    """

    name: str
    coord_mm: float
    direction: Direction  # "X" axis line or "Y" axis line family


@dataclass(frozen=True)
class ZoneAxisCheckResult:
    zone_index: int
    direction: Direction
    nearest_axis_x_min: str
    nearest_axis_x_max: str
    nearest_axis_y_min: str
    nearest_axis_y_max: str
    dist_x_min_mm: float
    dist_x_max_mm: float
    dist_y_min_mm: float
    dist_y_max_mm: float
    x_min_is_multiple_of_10: bool
    x_max_is_multiple_of_10: bool
    y_min_is_multiple_of_10: bool
    y_max_is_multiple_of_10: bool
    is_ok: bool


@dataclass(frozen=True)
class SlabGeometry:
    thickness_mm: float
    # protective layer (cover) from top/bottom faces to additional reinforcement steel.
    # Used to compute max allowed diameter.
    cover_top_mm: float
    cover_bottom_mm: float
    concrete_class: Literal["B15", "B20", "B25", "B30", "B35", "B40"]
    # For type SUM-31 vs SUM-33 vertical leg check
    min_clear_inside_mm: float = 10.0
    # Rebar bending radius check for A500; must be provided from your codebase/standards.
    # If unknown: start with a conservative factor (e.g. 4*D).
    a500_min_bend_radius_factor: float = 4.0


@dataclass(frozen=True)
class RNFAttributes:
    rnfc_division: str
    rnfc_design_mark: str
    rnfc_assembly_mark: str
    rnfc_element_mark: str
    alpha: float  # Коэф. α


@dataclass
class ZoneInstance:
    """
    One Revit family instance to be placed on the plan.

    For the first iteration we keep geometry as abstract:
    - start/end in long-axis and width across-perpendicular axis.
    """

    family_name: str
    direction: Direction

    # Start/end edges in mm along the long axis.
    long_start_mm: float
    long_end_mm: float
    # Zone width in mm (attribute "Ширина")
    width_mm: float
    # Bars across-perpendicular: computed from width_mm and step_mm
    bar_step_mm: int
    bar_count: int

    diameter_mm: int  # attribute "Ø"
    concrete_class: str  # "Класс бетона"
    alpha: float  # "Коэф. α"

    # RNF*
    rnfc: RNFAttributes

    # Spec accounting flags.
    include_in_schedule: bool = True  # "Учет в спецификации" for straight
    count_bars_only_for_bends: bool = False  # "подсчет стержней" for bends

    # Optional: additional notation for callouts ("П", "Г", "ГС")
    callout_short: Optional[str] = None
    callout_bar_count: Optional[int] = None
    # Full rectangular footprint in plan (mm).
    x_min_mm: Optional[float] = None
    x_max_mm: Optional[float] = None
    y_min_mm: Optional[float] = None
    y_max_mm: Optional[float] = None


@dataclass(frozen=True)
class CostModel:
    """
    Cost assumptions for comparative decision between detail levels.
    """

    steel_price_per_kg: float
    # Penalty (abstract money units) for constructability risk/complexity.
    complexity_penalty_per_point: float = 0.0
    # Weights of complexity components.
    zone_count_weight: float = 1.0
    overlap_joint_weight: float = 0.5
    bent_zone_weight: float = 1.5
    # Complexity scale thresholds for labels: min -> 2 -> 3 -> 4 -> max
    # Rule:
    #   score < t_min_2   => "min"
    #   score < t_2_3     => "2"
    #   score < t_3_4     => "3"
    #   score < t_4_max   => "4"
    #   else              => "max"
    t_min_2: float = 20.0
    t_2_3: float = 40.0
    t_3_4: float = 70.0
    t_4_max: float = 110.0


@dataclass(frozen=True)
class LevelCostSummary:
    detail_level: DetailLevel
    zone_count: int
    bent_zone_count: int
    overlap_joint_count: int
    total_bar_length_mm: float
    total_mass_kg: float
    steel_cost: float
    complexity_score: float
    complexity_scale: Literal["min", "2", "3", "4", "max"]
    complexity_penalty_cost: float
    total_cost: float


@dataclass(frozen=True)
class SliderConfig:
    """
    UI slider config: min..max -> exact..coarse mapping.
    slider_value is expected in [0.0, 1.0]:
      0.0 = min (most detailed)
      1.0 = max (most simplified)
    """

    # partition edges for discrete detail levels:
    # [0, edge_exact_medium) -> exact
    # [edge_exact_medium, edge_medium_coarse) -> medium
    # [edge_medium_coarse, 1] -> coarse
    edge_exact_medium: float = 0.34
    edge_medium_coarse: float = 0.67


def mermaid_flowchart_for_extra_rebar_layout() -> str:
    """
    Mermaid Live Editor block representation of the algorithm.
    """
    return r"""
flowchart TD
    A[Input: mosaic grid, slab geometry, holes, direction, RNF] --> B[1) Direction & cover-based max Ø]
    B --> C[2) Anchorage & lap lengths from tables\nanchorage=ceil10, lap=ceil50]
    C --> D[3) Compare exact/medium/coarse by cost + complexity risk]
    D --> E[4) Select target detail level]
    E --> F[5) Preprocess mosaic: smooth single-node spikes]
    F --> G{Local peaks found?}
    G -- No --> Z[6) Emit zone instances + RNF attributes\n(length/width/step/Ø/class/alpha)]
    G -- Yes --> H[5.1) Grow zone bounds around each peak\nthreshold depends on detail]
    H --> I[5.3) Width: bar_count then W=(bar_count-1)*bar_step]
    I --> J[5.1) Length: add anchorage\nL_required = span + 2*L_anc]
    J --> K{L_required <= 11700?}
    K -- Yes --> L[5) Create straight SUM-30 zone]
    K -- No --> M[5.5) Split into multiple SUM-3 segments\nwith double lap = 2*alpha*L_lap]
    L --> N[5.6) Check intersection with holes]
    M --> N
    N --> O{Hole size in perpendicular dir <=200?}
    O -- Yes --> P[Keep straight zone]
    O -- No --> Q[Place bent detailing (offset 50mm from hole edge)]
    Q --> R[Select bent family by vertical anchorage vs covers]
    P --> S[5.7) Set attributes + spec/schedule flags]
    R --> S
    S --> T[6) Dimensioning + callouts (P/G/GS + counts)]
    Z --> T
"""


@dataclass(frozen=True)
class SliderEvaluationResult:
    slider_value: float
    slider_label: Literal["min", "2", "3", "4", "max"]
    selected_detail_level: DetailLevel
    # Targets shown to user
    complexity_score: float
    complexity_scale: Literal["min", "2", "3", "4", "max"]
    economic_score: float
    steel_cost: float
    total_cost: float
    # Zones to display on plan for this slider position
    zones: List[ZoneInstance]

