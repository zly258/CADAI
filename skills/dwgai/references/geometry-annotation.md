# Geometry Creation, Editing, Dimensions & Blocks

## 1. Geometric Drafting

- **Lines & centerlines**: `cad_draw_line`, `cad_draw_centerline` (loads `CENTER` linetype, optional endpoint `extension`, default ACI red color `1`).
- **Rectangles & polylines**: `cad_draw_rectangle` (`x1/y1/x2/y2` or `cx/cy/width/height` -> closed 4-vertex polyline), `cad_draw_polyline` (supports `closed=true`).
- **Curves**: `cad_draw_circle`, `cad_draw_arc` (`cx, cy, radius, start_angle_degrees, end_angle_degrees`), `cad_draw_ellipse` (`major_radius, minor_radius, rotation_degrees`), `cad_draw_spline` (`points: [[x,y],...], closed, degree`).
- **Annotation & hatching**: `cad_draw_text`, `cad_draw_hatch` (closed entity handle + pattern name).
- Ensure target layer exists (`cad_layer_list` / `cad_layer_create`) before passing `layer`.

## 2. Entity Modification & Patterns

- **Properties**: `cad_entity_set_properties` supports `layer`, `color_index`, `linetype`, `lineweight` (mm e.g. `0.25` or integer e.g. `25`), and `linetype_scale`.
- **Transforms & copies**: `cad_entity_transform` (`move`/`rotate`/`scale`), `cad_entity_copy` (`dx, dy, dz, count`), `cad_entity_mirror` (`x1, y1, x2, y2, erase_source`), `cad_entity_offset` (`distance, side_x?, side_y?`).
- **Arrays & corners**: `cad_entity_array_rectangular` (`rows, columns, row_spacing, column_spacing`), `cad_entity_array_polar` (`center_x, center_y, count, total_angle_degrees, rotate_items`), `cad_entity_fillet_chamfer` (fillet arc or chamfer bevel between two `Line` handles, auto-trims), `cad_entity_explode`, `cad_entity_delete`.

## 3. Dimensioning & Leaders

- **Linear & chain**: `cad_dimension_linear_create` (`is_vertical`), `cad_dimension_aligned_create`, `cad_dimension_chain_create` (batch collinear dimensions across `points: [[x,y],...]`).
- **Radial, diametric, angular & ordinate**: `cad_dimension_diametric_create`, `cad_dimension_radial_create`, `cad_dimension_angular_create` (`vertex_x, vertex_y, x1, y1, x2, y2, arc_x, arc_y`), `cad_dimension_ordinate_create` (`using_x_axis, def_x, def_y, leader_x, leader_y`).
- **Multileaders & styles**: `cad_leader_create`, `cad_dimension_style_create`, `cad_dimension_style_set`.

## 4. Title Blocks & Block Attributes

- Inspect block defs & attributes with `cad_block_list` and `cad_block_get_attributes`.
- Update block attributes in place via `cad_block_set_attributes`, and insert blocks via `cad_block_insert`.
