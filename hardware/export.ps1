# Regenerates every STL file and preview image after changing technic.scad (calibration)
# or the dimensions of a part.
# Run from this folder:  powershell -ExecutionPolicy Bypass -File .\export.ps1

$openscad = "C:\Program Files\OpenSCAD\openscad.com"
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force stl, images | Out-Null

# (the \" are needed: Windows PowerShell drops the quotes otherwise)
$parts = @(
    @{ file = "calibration.scad";      stl = "calibration.stl" },
    @{ file = "transmitter_case.scad"; stl = "transmitter_case.stl";  option = 'part=\"case\"' },
    @{ file = "transmitter_case.scad"; stl = "transmitter_lid.stl";   option = 'part=\"lid\"' },
    @{ file = "transmitter_case.scad"; stl = "nrf_cradle.stl";        option = 'part=\"cradle\"' }
)

foreach ($p in $parts) {
    Write-Host "Exporting $($p.stl)..."
    $arguments = @("-o", "stl\$($p.stl)")
    if ($p.option) { $arguments += @("-D", $p.option) }
    $arguments += $p.file
    & $openscad @arguments
}

$images = @(
    @{ png = "assembly.png"; option = 'part=\"assembly\"'; camera = "36,32,58,62,0,320,470"; size = "1000,1000" },
    @{ png = "case.png";     option = 'part=\"case\"';     camera = "36,32,10,50,0,140,240" },
    @{ png = "lid.png";      option = 'part=\"lid\"';      camera = "36,32,0,45,0,30,220" },
    @{ png = "cradle.png";   option = 'part=\"cradle\"';   camera = "30,14,5,50,0,320,130" }
)

foreach ($i in $images) {
    Write-Host "Rendering $($i.png)..."
    & $openscad -o "images\$($i.png)" -D $i.option "--imgsize=$(if ($i.size) { $i.size } else { '1000,750' })" "--camera=$($i.camera)" `
        --colorscheme=Tomorrow transmitter_case.scad
}
Write-Host "Done: STL files are in the stl folder, previews in the images folder."
