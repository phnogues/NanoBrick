// Technic helpers shared by all parts
// ===================================
// Print "calibration.scad" first, then put here the values that fit YOUR pins and axles.
// (Copied from the LearnCode "barrier" project so that this repository has no outside dependency.)

// ---- Calibrated values (PETG on a Creality K2, 0.4 nozzle: hole 4 of the calibration part) ----
pin_hole_d = 5.0;   // round hole for a Technic pin (LEGO: 4.8)
axle_w     = 5.0;   // cross hole: overall width (LEGO axle: 4.8)
axle_arm   = 2.0;   // cross hole: width of one arm (LEGO axle: 1.8)

// ---- LEGO Technic dimensions (do not change) ----
pitch       = 8;     // distance between two holes
beam_t      = 7.8;   // beam thickness (along the holes)
beam_h      = 7.4;   // beam height (rounded end)
cb_d        = 6.2;   // counterbore around a hole, for the pin collar
cb_depth    = 0.8;
clearance   = 0.2;   // gap between two neighbouring parts

$fn = 48;

// Through round hole of height h (along Z, from z = 0), with counterbores.
// cb_bottom / cb_top: false when that face is not a bearing face.
module pin_hole(h = beam_t, cb_bottom = true, cb_top = true) {
    translate([0, 0, -0.01]) cylinder(d = pin_hole_d, h = h + 0.02);
    if (cb_bottom)
        translate([0, 0, -0.01]) cylinder(d = cb_d, h = cb_depth + 0.01);
    if (cb_top)
        translate([0, 0, h - cb_depth]) cylinder(d = cb_d, h = cb_depth + 0.01);
}

// Through cross hole of height h (along Z, from z = 0).
module axle_hole(h = beam_t) {
    translate([0, 0, h / 2]) {
        cube([axle_w, axle_arm, h + 0.02], center = true);
        cube([axle_arm, axle_w, h + 0.02], center = true);
    }
}

// Grid of nx × ny round holes, the first one at (0, 0).
module hole_grid(nx, ny, h = beam_t, cb_bottom = true, cb_top = true) {
    for (i = [0 : nx - 1], j = [0 : ny - 1])
        translate([i * pitch, j * pitch, 0]) pin_hole(h, cb_bottom, cb_top);
}

// Box of size [x, y, z] with rounded vertical edges (radius r), corner at (0, 0, 0).
module rounded_box(size, r = 2) {
    hull()
        for (x = [r, size[0] - r], y = [r, size[1] - r])
            translate([x, y, 0]) cylinder(r = r, h = size[2]);
}
