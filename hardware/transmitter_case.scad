// nanoBrick transmitter case: ESP32-32U DevKit (38 pins) + nRF24L01+ PA+LNA on its adapter
// =========================================================================================
// - The floor is a Technic plate (vertical holes), and both long sides have a row of
//   horizontal Technic holes: the case can lie flat or stand against a build.
// - ESP32 flat, components up, Dupont wires below. USB-C reachable at end 1.
// - nRF24 stack standing on its edge, in its own small cradle (part "cradle", quick to reprint).
//   Its soldered SMA elbow goes through a U slot in end 1; the lid closes the slot.
// - Wi-Fi antenna (u.FL pigtail to a bulkhead connector with a nut) through a hole in end 2.
//   The spare pigtail is wound around two posts.
// - Both antennas fold upright with their hinge, about 8 cm apart. No metal near them.
// - Clip-on lid, no screws. Fingernail notch at end 2.
//
// Print in PETG, all three parts without supports:
//   - the case standing on its floor;
//   - the lid outer face down (the "lid" part is already flipped);
//   - the cradle on its base.
// Pick the part with "part" below, or export everything with export.ps1.
//
// Coordinates: X along the case (end 1 = USB side at x = 0), Y across, Z up.
// Positions inside the case are given from the inner corner (end 1, nRF side, floor top).

include <technic.scad>

part = "all";   // "case", "lid", "cradle", "all" (print layout) or "assembly" (preview with boards)

// ---- Case size, in Technic holes ----
nx       = 9;      // along X
ny       = 8;      // along Y
wall     = 2.4;
inner_h  = 29;     // inner height above the floor
lid_t    = 2;

// ---- ESP32 DevKit 38 pins (measured) ----
esp_l       = 48;    // board length, without the USB-C socket
esp_w       = 28;
esp_pcb     = 1.6;
esp_total_h = 25;    // from the bent Dupont wires to the top of the components
esp_above   = 3.4;   // components above the board (module, USB-C socket)
usb_out     = 3;     // USB-C socket sticking out of the board end
usb_w       = 12;    // opening for the cable plug overmold
usb_h       = 7;
esp_gap     = 0.5;   // clearance between the board and the side wall
can_x       = 38;    // lid presser on the module shield, from the USB end of the board

// ---- Wi-Fi antenna bulkhead connector (end 2) ----
wifi_hole = 6.6;     // thread is 6 mm
wifi_z    = 18;      // hole axis above the floor
wifi_nut  = 8;       // nut across flats (clearance check only)

// ---- nRF24 stack, standing on its edge ----
nrf_l        = 59;   // from the far end of the adapter to the face of the SMA elbow
adapt_l      = 28;
adapt_w      = 20;   // adapter board width = its height once standing
module_l     = 46;
module_w     = 16;
board_gap    = 12;   // between the adapter board and the module board
module_dz    = 3;    // module bottom edge above the adapter bottom edge (test prints 1-2, 2026-11-27)
sma_off      = 8;    // SMA axis to the module board (away from the adapter)
sma_from_edge = 8;   // SMA axis above the module bottom edge
sma_hole     = 6.6;  // thread is 6 mm
elbow_out    = 3;    // SMA elbow face beyond the module end
sma_wall     = 1.5;  // wall thickness around the SMA (the thread is short)
cradle_lift  = 2;    // adapter bottom edge above the floor = cradle base (its slot goes down to the base)
slot_depth   = 5;    // how deep the module edge sits in its cradle slot
adapt_depth  = 8;    // same for the adapter: deeper, to grip it better (both slot tops at the same height)
// Cradle slots, as [start, length] along each board. They avoid the solder joints and the
// Dupont plugs that sit near the bottom edges (read from the test print photos, 2026-11-27).
adapt_slot   = [12.5, 6];  // from the adapter end on the module side
module_slot  = [12, 12];   // from the module end on the SMA side
adapt_y      = 2;    // adapter board back face, from the nRF side wall

// ---- Clips ----
tab_h     = 5;       // lid tabs, down from the wall top
tab_t     = 1.2;
bump      = 0.6;     // clip bump height
clip_z    = 3.5;     // bump position below the wall top

// ---- Derived values ----
ext_l  = nx * pitch - clearance;
ext_w  = ny * pitch - clearance;
in_l   = ext_l - 2 * wall;
in_w   = ext_w - 2 * wall;
floor_t = beam_t;                 // the floor is a Technic plate
wall_h = floor_t + inner_h;

esp_x0    = usb_out - wall;       // the USB-C socket face is flush with the outside
esp_y0    = in_w - esp_gap - esp_w;
esp_below = esp_total_h - esp_above - esp_pcb;
usb_y     = esp_y0 + esp_w / 2;
usb_z     = esp_below + esp_pcb + 1.6;

elbow_x   = -(wall - sma_wall);   // elbow face, in the thinned wall
adapt_x0  = elbow_x + nrf_l - adapt_l;
module_x0 = elbow_x + elbow_out;
module_y  = adapt_y + board_gap;
sma_y     = module_y + sma_off;
sma_z     = cradle_lift + module_dz + sma_from_edge;

posts     = [[in_l - 12, 40], [in_l - 4, 40]];   // pigtail posts (x, y)
nail_y    = [6, 18];                            // fingernail notch on end 2

// Lid tabs: [side, from, to] ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â side "y0" (nRF wall), "y1" (ESP wall), "x1" (end 2)
tabs = [["y0", 6, 20], ["y1", 8, 22], ["y1", 40, 54], ["x1", 24, 36]];

assert(esp_x0 >= 0, "USB socket sticks out more than the wall: check usb_out");
assert(elbow_x + nrf_l + 0.5 <= in_l, "nRF stack too long: increase nx");
assert(sma_y + 5 < esp_y0, "nRF stack and ESP32 overlap: increase ny");
assert(wifi_z - wifi_nut / 2 - 1 > 11.5, "Wi-Fi nut hits the pigtail posts: raise wifi_z");
assert(wifi_z + wifi_nut / 2 + 1 < inner_h - tab_h, "Wi-Fi nut hits the lid tab: lower wifi_z");

// inside coordinates ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ case coordinates
module inside() { translate([wall, wall, floor_t]) children(); }

// ---------------------------------------------------------------- case
module case() {
    difference() {
        rounded_box([ext_l, ext_w, wall_h], r = 2);

        // inner space
        inside() cube([in_l, in_w, inner_h + 1]);

        // vertical Technic holes in the floor (the two edge rows are used by the side holes)
        translate([pitch / 2 - clearance / 2, pitch / 2 - clearance / 2 + pitch, 0])
            hole_grid(nx, ny - 2, h = floor_t, cb_top = false);

        // horizontal Technic holes along both long sides, in the floor edge
        for (i = [0 : nx - 1]) {
            x = pitch / 2 - clearance / 2 + i * pitch;
            translate([x, 0, floor_t / 2]) rotate([-90, 0, 0])
                pin_hole(h = beam_t, cb_top = false);
            translate([x, ext_w, floor_t / 2]) rotate([90, 0, 0])
                pin_hole(h = beam_t, cb_top = false);
        }

        // end 1: USB-C notch (open at the top, closed by the lid)
        inside() translate([-wall - 0.01, usb_y - usb_w / 2, usb_z - usb_h / 2])
            cube([wall + 0.02, usb_w, inner_h]);

        // end 1: SMA U slot, and the wall thinned around it
        inside() {
            translate([-wall - 0.01, sma_y - sma_hole / 2, sma_z])
                cube([wall + 0.02, sma_hole, inner_h]);
            translate([-wall - 0.01, sma_y, sma_z]) rotate([0, 90, 0])
                cylinder(d = sma_hole, h = wall + 0.02);
            // extra 1 mm below the thread, in case the stack sits lower
            translate([-wall - 0.01, sma_y - sma_hole / 2, sma_z - sma_hole / 2 - 1])
                cube([wall + 0.02, sma_hole, sma_hole / 2 + 1]);
            translate([elbow_x, sma_y - 6, sma_z - 6]) cube([wall, 12, inner_h]);
        }

        // end 2: Wi-Fi bulkhead hole
        inside() translate([in_l - 0.01, usb_y, wifi_z]) rotate([0, 90, 0])
            cylinder(d = wifi_hole, h = wall + 0.02);

        // end 2: fingernail notch to open the lid
        translate([ext_l - wall - 0.01, wall + nail_y[0], wall_h - 2])
            cube([wall + 0.02, nail_y[1] - nail_y[0], 2.01]);

        // clip grooves, in front of each lid tab
        for (t = tabs) clip_groove(t);
    }

    // ESP32 supports: four pillars under the board ends, between the pin rows
    // (the wires pass between them), and a stop at the far end of the board
    inside() translate([0, 0, -0.01]) {
        for (x = [esp_x0, esp_x0 + esp_l - 3], y = [esp_y0 + 5, esp_y0 + esp_w - 9])
            translate([x, y, 0]) cube([3, 4, esp_below + 0.01]);
        for (y = [esp_y0 + 5, esp_y0 + esp_w - 9])
            translate([esp_x0 + esp_l + 0.3, y, 0]) cube([1.5, 4, esp_below + esp_pcb + 1.01]);
    }

    // posts to wind the spare Wi-Fi pigtail
    inside() for (p = posts) translate([p[0], p[1], -0.01]) {
        cylinder(d = 4, h = 10.01);
        translate([0, 0, 9]) cylinder(d1 = 4, d2 = 7, h = 1.51);
    }
}

module clip_groove(t) {
    gz = wall_h - clip_z;
    len = t[2] - t[1] + 1;
    if (t[0] == "y0") translate([wall + t[1] - 0.5, wall - bump - 0.2, gz - 0.9]) cube([len, bump + 0.21, 1.8]);
    if (t[0] == "y1") translate([wall + t[1] - 0.5, wall + in_w - 0.01, gz - 0.9]) cube([len, bump + 0.21, 1.8]);
    if (t[0] == "x1") translate([wall + in_l - 0.01, wall + t[1] - 0.5, gz - 0.9]) cube([bump + 0.21, len, 1.8]);
}

// ---------------------------------------------------------------- lid (as installed)
module lid_installed() {
    top = wall_h;
    difference() {
        union() {
            translate([0, 0, top]) rounded_box([ext_l, ext_w, lid_t], r = 2);

            // clip tabs with their bump
            for (t = tabs) lid_tab(t);

            inside() {
                // tongue closing the SMA slot, with a half-round cut for the thread
                difference() {
                    translate([-wall + 0.15, sma_y - sma_hole / 2 + 0.15, sma_z])
                        cube([wall - 0.15, sma_hole - 0.3, inner_h - sma_z]);
                    translate([-wall - 0.01, sma_y, sma_z]) rotate([0, 90, 0])
                        cylinder(d = sma_hole, h = wall + 0.02);
                }
                // tongue closing the top of the USB notch
                translate([-wall + 0.15, usb_y - usb_w / 2 + 0.15, usb_z + usb_h / 2])
                    cube([wall - 0.15, usb_w - 0.3, inner_h - usb_z - usb_h / 2]);

                // pressers on the ESP32: USB socket and module shield
                for (x = [esp_x0 + 1, esp_x0 + can_x])
                    translate([x, usb_y - 2.5, esp_total_h]) cube([4, 5, inner_h - esp_total_h]);

                // pressers on the nRF stack, with a slot for the board edge
                presser([adapt_x0 + 8, adapt_y], cradle_lift + adapt_w);
                presser([module_x0 + 3, module_y], cradle_lift + module_dz + module_w);
            }
        }
    }
}

// presser hanging from the lid down to height z, slot centred on a board (back face at p[1])
module presser(p, z) {
    difference() {
        translate([p[0], p[1] - 2, z - 2]) cube([6, 1.6 + 4, inner_h - z + 2]);
        translate([p[0] - 0.01, p[1] - 0.3, z - 2.01]) cube([6.02, 1.6 + 0.6, 2.01]);
    }
}

module lid_tab(t) {
    top = wall_h;
    len = t[2] - t[1];
    bz = top - clip_z;
    if (t[0] == "y0") {
        translate([wall + t[1], wall + 0.2, top - tab_h]) cube([len, tab_t, tab_h]);
        translate([wall + t[1], wall + 0.2, bz]) rotate([0, 90, 0]) cylinder(r = bump, h = len, $fn = 16);
    }
    if (t[0] == "y1") {
        translate([wall + t[1], wall + in_w - 0.2 - tab_t, top - tab_h]) cube([len, tab_t, tab_h]);
        translate([wall + t[1], wall + in_w - 0.2, bz]) rotate([0, 90, 0]) cylinder(r = bump, h = len, $fn = 16);
    }
    if (t[0] == "x1") {
        translate([wall + in_l - 0.2 - tab_t, wall + t[1], top - tab_h]) cube([tab_t, len, tab_h]);
        translate([wall + in_l - 0.2, wall + t[1], bz]) rotate([-90, 0, 0]) cylinder(r = bump, h = len, $fn = 16);
    }
}

// lid flipped for printing (outer face on the bed)
module lid() {
    translate([0, ext_w, wall_h + lid_t]) rotate([180, 0, 0]) lid_installed();
}

// ---------------------------------------------------------------- nRF cradle
// Sits in the corner of the case (end 1, nRF side). Holds the bottom edges of the adapter
// and of the module; the lid presses on their top edges, and the SMA elbow sits in its slot.
cradle_t = min(cradle_lift, cradle_lift + module_dz);  // the lower board's slot goes down to the base
module cradle() {
    // base
    translate([0.3, 0.3, 0]) cube([adapt_x0 + adapt_l + 1, sma_y + 4, cradle_t]);
    cradle_slot([adapt_x0 + adapt_slot[0], adapt_y], adapt_slot[1], cradle_lift, adapt_depth, 1);
    cradle_slot([module_x0 + module_slot[0], module_y], module_slot[1], cradle_lift + module_dz, slot_depth, 0);
}

// A slotted block holding a board edge: p = [x start, board back face y], z = board bottom edge
// extra: slot made wider on the +y side (adapter: its components rub, test print 2)
module cradle_slot(p, len, z, depth, extra) {
    slot = 1.6 + 0.6 + extra;
    difference() {
        translate([p[0], p[1] - 1.5, 0]) cube([len, 1.6 + 3 + extra, z + depth]);
        translate([p[0] - 0.01, p[1] - 0.3, z]) cube([len + 0.02, slot, depth + 1]);
    }
}

// ---------------------------------------------------------------- boards, for the preview only
module boards() {
    inside() {
        // ESP32
        color("DimGray") translate([esp_x0, esp_y0, esp_below]) cube([esp_l, esp_w, esp_pcb]);
        color("Silver") translate([esp_x0 - usb_out, usb_y - 4.5, esp_below + esp_pcb]) cube([7.5, 9, 3.2]);
        color("Silver") translate([esp_x0 + 30, esp_y0 + 6, esp_below + esp_pcb]) cube([16, 16, 3.2]);
        // nRF adapter and module, on their edge
        color("DarkSlateGray") translate([adapt_x0, adapt_y, cradle_lift]) cube([adapt_l, 1.6, adapt_w]);
        color("DarkSlateGray") translate([module_x0, module_y, cradle_lift + module_dz]) cube([module_l, 1.6, module_w]);
        color("Gold") translate([elbow_x, sma_y, sma_z]) rotate([0, -90, 0]) cylinder(d = 6, h = 6);
        color("Gold") translate([elbow_x, sma_y - 3, sma_z - 3]) cube([4, 6, 6]);
        // Wi-Fi bulkhead
        color("Gold") translate([in_l - 8, usb_y, wifi_z]) rotate([0, 90, 0]) cylinder(d = 6, h = 16);
    }
}

if (part == "case") case();
if (part == "lid") lid();
if (part == "cradle") cradle();
if (part == "all") {
    case();
    translate([0, ext_w + 8, 0]) lid();
    translate([ext_l + 8, 0, 0]) cradle();
}
if (part == "assembly") {
    case();
    inside() cradle();
    boards();
    %lid_installed();
}
