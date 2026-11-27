// Calibration part: print it FIRST (about 25 minutes, a few grams)
// ================================================================
// Push a black Technic pin into each round hole, and an axle into each cross hole.
// Pick the hole that grips "like real LEGO" (firm, without forcing),
// then put its value in technic.scad (pin_hole_d, axle_w).
// Engraved numbers: 1 = 4.7 mm, 2 = 4.8, 3 = 4.9, 4 = 5.0, 5 = 5.1.
//
// Print flat, no supports. Print it again when you change filament (PETG and PLA differ).

include <technic.scad>

round_d = [4.7, 4.8, 4.9, 5.0, 5.1];   // tested round holes
cross_w = [4.7, 4.8, 4.9, 5.0, 5.1];   // tested cross holes

n = len(round_d);
spacing = 10;      // spacing of the test holes (no need to follow the Technic grid)
row = 13;          // distance between the round row and the cross row
length = (n - 1) * spacing + beam_h + 2;

difference() {
    translate([-beam_h / 2 - 1, -beam_h / 2 - 1, 0])
        rounded_box([length, row + beam_h + 2, beam_t], r = 2);

    for (i = [0 : n - 1]) {
        translate([i * spacing, 0, -0.01]) cylinder(d = round_d[i], h = beam_t + 0.02);
        translate([i * spacing, row, beam_t / 2]) {
            cube([cross_w[i], axle_arm, beam_t + 0.02], center = true);
            cube([axle_arm, cross_w[i], beam_t + 0.02], center = true);
        }
    }

    // engraved numbers: 1 = smallest
    for (i = [0 : n - 1])
        translate([i * spacing, row / 2, beam_t - 0.6])
            linear_extrude(1)
                text(str(i + 1), size = 3.2, halign = "center", valign = "center",
                     font = "Liberation Sans:style=Bold");
}
