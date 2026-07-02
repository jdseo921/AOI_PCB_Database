param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot "ImageLearningDemo_Project"),
    [int]$GoldenCount = 3,
    [int]$OkLearningCount = 40,
    [int]$OkValidationCount = 30,
    [int]$InspectionCount = 20,
    [int]$NgValidationCount = 20,
    [int]$Seed = 42
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

foreach ($item in @(
    @{ Name = "GoldenCount"; Value = $GoldenCount },
    @{ Name = "OkLearningCount"; Value = $OkLearningCount },
    @{ Name = "OkValidationCount"; Value = $OkValidationCount },
    @{ Name = "InspectionCount"; Value = $InspectionCount },
    @{ Name = "NgValidationCount"; Value = $NgValidationCount }
)) {
    if ($item.Value -lt 0) {
        throw "$($item.Name) must be zero or greater."
    }
}

Add-Type -AssemblyName System.Drawing

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$roleFolders = [ordered]@{
    golden = Join-Path $OutputRoot "golden"
    ok_learning = Join-Path $OutputRoot "ok_learning"
    ok_validation = Join-Path $OutputRoot "ok_validation"
    inspection = Join-Path $OutputRoot "inspection"
    ng_validation = Join-Path $OutputRoot "ng_validation"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
foreach ($folder in $roleFolders.Values) {
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    Get-ChildItem -LiteralPath $folder -Filter "synthetic_*.png" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

function Clamp-Byte {
    param([int]$Value)
    return [byte]([Math]::Max(0, [Math]::Min(255, $Value)))
}

function New-Color {
    param(
        [int]$R,
        [int]$G,
        [int]$B,
        [int]$Brightness = 0,
        [int]$ColorShift = 0,
        [int]$Alpha = 255
    )

    return [System.Drawing.Color]::FromArgb(
        [Math]::Max(0, [Math]::Min(255, $Alpha)),
        (Clamp-Byte ($R + $Brightness + $ColorShift)),
        (Clamp-Byte ($G + $Brightness)),
        (Clamp-Byte ($B + $Brightness - $ColorShift)))
}

function New-Brush {
    param(
        [int]$R,
        [int]$G,
        [int]$B,
        [int]$Brightness = 0,
        [int]$ColorShift = 0,
        [int]$Alpha = 255
    )

    return [System.Drawing.SolidBrush]::new((New-Color $R $G $B $Brightness $ColorShift $Alpha))
}

function New-Pen {
    param(
        [int]$R,
        [int]$G,
        [int]$B,
        [single]$Width,
        [int]$Brightness = 0,
        [int]$ColorShift = 0,
        [int]$Alpha = 255
    )

    return [System.Drawing.Pen]::new((New-Color $R $G $B $Brightness $ColorShift $Alpha), $Width)
}

function New-Rect {
    param([int]$X, [int]$Y, [int]$Width, [int]$Height)
    return [System.Drawing.Rectangle]::new($X, $Y, $Width, $Height)
}

function Draw-Label {
    param(
        [System.Drawing.Graphics]$Graphics,
        [string]$Text,
        [int]$X,
        [int]$Y,
        [System.Drawing.Font]$Font,
        [int]$Brightness,
        [int]$ColorShift
    )

    $brush = New-Brush 226 238 231 $Brightness $ColorShift 220
    try {
        $Graphics.DrawString($Text, $Font, $brush, [single]$X, [single]$Y)
    }
    finally {
        $brush.Dispose()
    }
}

function Draw-IntegratedCircuit {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Rectangle]$Rect,
        [string]$Label,
        [System.Drawing.Font]$Font,
        [int]$Brightness,
        [int]$ColorShift
    )

    $bodyBrush = New-Brush 24 29 31 $Brightness 0
    $outlinePen = New-Pen 102 119 126 2 $Brightness 0
    $pinBrush = New-Brush 198 180 118 $Brightness $ColorShift
    $notchBrush = New-Brush 58 70 72 $Brightness 0
    try {
        $Graphics.FillRectangle($bodyBrush, $Rect)
        $Graphics.DrawRectangle($outlinePen, $Rect)
        $Graphics.FillEllipse($notchBrush, $Rect.X + [int]($Rect.Width / 2) - 8, $Rect.Y + 5, 16, 10)
        for ($i = 0; $i -lt 8; $i++) {
            $pinY = $Rect.Y + 9 + ($i * 9)
            $Graphics.FillRectangle($pinBrush, $Rect.X - 8, $pinY, 8, 4)
            $Graphics.FillRectangle($pinBrush, $Rect.Right, $pinY, 8, 4)
        }

        Draw-Label $Graphics $Label ($Rect.X + 28) ($Rect.Y + 32) $Font $Brightness $ColorShift
    }
    finally {
        $bodyBrush.Dispose()
        $outlinePen.Dispose()
        $pinBrush.Dispose()
        $notchBrush.Dispose()
    }
}

function Draw-Resistor {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Rectangle]$Rect,
        [int]$Brightness,
        [int]$ColorShift
    )

    $bodyBrush = New-Brush 190 161 103 $Brightness $ColorShift
    $leadPen = New-Pen 174 179 169 3 $Brightness 0
    $bandBrush = New-Brush 84 54 31 $Brightness 0
    try {
        $centerY = $Rect.Y + [int]($Rect.Height / 2)
        $Graphics.DrawLine($leadPen, $Rect.X - 16, $centerY, $Rect.X, $centerY)
        $Graphics.DrawLine($leadPen, $Rect.Right, $centerY, $Rect.Right + 16, $centerY)
        $Graphics.FillRectangle($bodyBrush, $Rect)
        $Graphics.FillRectangle($bandBrush, $Rect.X + 8, $Rect.Y, 4, $Rect.Height)
        $Graphics.FillRectangle($bandBrush, $Rect.X + 22, $Rect.Y, 4, $Rect.Height)
        $Graphics.FillRectangle($bandBrush, $Rect.X + 36, $Rect.Y, 4, $Rect.Height)
    }
    finally {
        $bodyBrush.Dispose()
        $leadPen.Dispose()
        $bandBrush.Dispose()
    }
}

function Draw-Capacitor {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Rectangle]$Rect,
        [int]$Brightness,
        [int]$ColorShift,
        [bool]$ReversedPolarity = $false
    )

    $bodyBrush = New-Brush 95 114 124 $Brightness $ColorShift
    $stripeBrush = New-Brush 226 238 231 $Brightness 0
    $leadPen = New-Pen 172 178 170 3 $Brightness 0
    try {
        $centerY = $Rect.Y + [int]($Rect.Height / 2)
        $Graphics.DrawLine($leadPen, $Rect.X - 12, $centerY, $Rect.X, $centerY)
        $Graphics.DrawLine($leadPen, $Rect.Right, $centerY, $Rect.Right + 12, $centerY)
        $Graphics.FillRectangle($bodyBrush, $Rect)
        $stripeX = if ($ReversedPolarity) { $Rect.Right - 8 } else { $Rect.X + 4 }
        $Graphics.FillRectangle($stripeBrush, $stripeX, $Rect.Y + 2, 4, $Rect.Height - 4)
    }
    finally {
        $bodyBrush.Dispose()
        $stripeBrush.Dispose()
        $leadPen.Dispose()
    }
}

function Add-VisualNoise {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Random]$Random,
        [int]$Width,
        [int]$Height,
        [int]$NoiseLevel
    )

    $dotCount = [Math]::Max(0, $NoiseLevel * 120)
    for ($i = 0; $i -lt $dotCount; $i++) {
        $alpha = $Random.Next(8, 26)
        $value = if ($Random.NextDouble() -gt 0.5) { 255 } else { 0 }
        $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb($alpha, $value, $value, $value))
        try {
            $x = $Random.Next(0, $Width)
            $y = $Random.Next(0, $Height)
            $size = $Random.Next(1, 3)
            $Graphics.FillRectangle($brush, $x, $y, $size, $size)
        }
        finally {
            $brush.Dispose()
        }
    }
}

function Write-BoardImage {
    param(
        [string]$Path,
        [System.Random]$Random,
        [int]$Serial,
        [string]$Role,
        [string]$AnomalyKind,
        [int]$Brightness,
        [int]$ShiftX,
        [int]$ShiftY,
        [int]$NoiseLevel,
        [int]$ColorShift
    )

    $width = 640
    $height = 420
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $font = [System.Drawing.Font]::new("Segoe UI", 11, [System.Drawing.FontStyle]::Regular)
    $smallFont = [System.Drawing.Font]::new("Segoe UI", 8, [System.Drawing.FontStyle]::Regular)
    $labelFont = [System.Drawing.Font]::new("Consolas", 9, [System.Drawing.FontStyle]::Bold)

    try {
        $surfaceBrush = New-Brush 5 12 12
        $boardBrush = New-Brush 16 91 48 $Brightness $ColorShift
        $boardAltBrush = New-Brush 20 106 56 $Brightness $ColorShift
        $boardPen = New-Pen 84 155 95 3 $Brightness $ColorShift
        $gridPen = New-Pen 48 128 69 1 $Brightness $ColorShift 120
        $tracePen = New-Pen 196 157 68 4 $Brightness $ColorShift
        $thinTracePen = New-Pen 191 151 62 2 $Brightness $ColorShift
        $silkPen = New-Pen 218 238 226 1 $Brightness 0 210
        $padBrush = New-Brush 199 174 91 $Brightness $ColorShift

        try {
            $Graphics = $graphics
            $Graphics.FillRectangle($surfaceBrush, 0, 0, $width, $height)
            $boardRect = New-Rect (42 + $ShiftX) (30 + $ShiftY) 552 356
            $Graphics.FillRectangle($boardBrush, $boardRect)
            $Graphics.FillRectangle($boardAltBrush, $boardRect.X + 12, $boardRect.Y + 12, $boardRect.Width - 24, $boardRect.Height - 24)
            $Graphics.DrawRectangle($boardPen, $boardRect)

            for ($x = $boardRect.X + 32; $x -lt $boardRect.Right; $x += 36) {
                $Graphics.DrawLine($gridPen, $x, $boardRect.Y + 6, $x, $boardRect.Bottom - 6)
            }

            for ($y = $boardRect.Y + 30; $y -lt $boardRect.Bottom; $y += 36) {
                $Graphics.DrawLine($gridPen, $boardRect.X + 6, $y, $boardRect.Right - 6, $y)
            }

            $left = $boardRect.X
            $top = $boardRect.Y

            $Graphics.DrawLine($tracePen, $left + 82, $top + 76, $left + 226, $top + 76)
            $Graphics.DrawLine($tracePen, $left + 226, $top + 76, $left + 226, $top + 140)
            $Graphics.DrawLine($tracePen, $left + 333, $top + 145, $left + 492, $top + 145)
            $Graphics.DrawLine($tracePen, $left + 492, $top + 145, $left + 492, $top + 254)
            $Graphics.DrawLine($thinTracePen, $left + 102, $top + 238, $left + 226, $top + 182)
            $Graphics.DrawLine($thinTracePen, $left + 333, $top + 184, $left + 470, $top + 285)
            $Graphics.DrawLine($thinTracePen, $left + 82, $top + 292, $left + 520, $top + 292)
            $Graphics.DrawLine($thinTracePen, $left + 138, $top + 84, $left + 138, $top + 292)

            $padPoints = @(
                @(82, 76), @(138, 84), @(226, 76), @(492, 145), @(492, 254),
                @(102, 238), @(470, 285), @(82, 292), @(520, 292), @(426, 78),
                @(426, 110), @(470, 78), @(470, 110)
            )
            foreach ($point in $padPoints) {
                $Graphics.FillEllipse($padBrush, $left + $point[0] - 8, $top + $point[1] - 8, 16, 16)
            }

            Draw-IntegratedCircuit $Graphics (New-Rect ($left + 234) ($top + 106) 92 82) "U1" $font $Brightness $ColorShift
            Draw-IntegratedCircuit $Graphics (New-Rect ($left + 72) ($top + 148) 70 56) "U2" $smallFont $Brightness $ColorShift

            Draw-Resistor $Graphics (New-Rect ($left + 380) ($top + 132) 52 16) $Brightness $ColorShift
            Draw-Resistor $Graphics (New-Rect ($left + 152) ($top + 246) 52 16) $Brightness $ColorShift
            Draw-Capacitor $Graphics (New-Rect ($left + 414) ($top + 72) 44 30) $Brightness $ColorShift
            Draw-Capacitor $Graphics (New-Rect ($left + 446) ($top + 236) 44 30) $Brightness $ColorShift

            $oscBrush = New-Brush 42 50 55 $Brightness 0
            $ledBrush = New-Brush 159 30 34 $Brightness 0
            try {
                $Graphics.FillRectangle($oscBrush, $left + 308, $top + 256, 60, 34)
                $Graphics.DrawRectangle($silkPen, $left + 304, $top + 252, 68, 42)
                $Graphics.FillEllipse($ledBrush, $left + 505, $top + 70, 24, 24)
            }
            finally {
                $oscBrush.Dispose()
                $ledBrush.Dispose()
            }

            $Graphics.DrawRectangle($silkPen, $left + 64, $top + 62, 42, 28)
            $Graphics.DrawRectangle($silkPen, $left + 372, $top + 122, 70, 34)
            $Graphics.DrawRectangle($silkPen, $left + 406, $top + 64, 60, 46)
            $Graphics.DrawLine($silkPen, $left + 500, $top + 64, $left + 534, $top + 98)
            Draw-Label $Graphics "AOI IMG-LEARN SYNTH" ($left + 176) ($top + 20) $labelFont $Brightness $ColorShift
            Draw-Label $Graphics ("SN{0:0000}" -f $Serial) ($left + 440) ($top + 316) $smallFont $Brightness $ColorShift
            Draw-Label $Graphics $Role ($left + 70) ($top + 316) $smallFont $Brightness $ColorShift

            switch ($AnomalyKind) {
                "SolderBridge" {
                    $bridgePen = New-Pen 236 232 190 11 $Brightness 0 230
                    try {
                        $Graphics.DrawLine($bridgePen, $left + 326, $top + 128, $left + 382, $top + 140)
                        $Graphics.DrawEllipse($bridgePen, $left + 343, $top + 126, 34, 20)
                    }
                    finally {
                        $bridgePen.Dispose()
                    }
                }
                "MissingComponent" {
                    $clearBrush = New-Brush 20 106 56 $Brightness $ColorShift
                    $warningPen = New-Pen 226 238 231 2 $Brightness 0
                    try {
                        $Graphics.FillRectangle($clearBrush, $left + 148, $top + 240, 62, 28)
                        $Graphics.DrawRectangle($warningPen, $left + 148, $top + 240, 62, 28)
                        $Graphics.DrawLine($warningPen, $left + 148, $top + 240, $left + 210, $top + 268)
                        $Graphics.DrawLine($warningPen, $left + 210, $top + 240, $left + 148, $top + 268)
                    }
                    finally {
                        $clearBrush.Dispose()
                        $warningPen.Dispose()
                    }
                }
                "ShiftedComponent" {
                    $ghostPen = New-Pen 226 238 231 2 $Brightness 0 180
                    $exposedPadBrush = New-Brush 236 232 190 $Brightness 0 240
                    $emptyFootprintBrush = New-Brush 18 82 44 $Brightness $ColorShift 235
                    try {
                        $Graphics.FillRectangle($emptyFootprintBrush, $left + 372, $top + 124, 72, 32)
                        $Graphics.FillEllipse($exposedPadBrush, $left + 374, $top + 134, 14, 14)
                        $Graphics.FillEllipse($exposedPadBrush, $left + 428, $top + 134, 14, 14)
                        $Graphics.DrawRectangle($ghostPen, $left + 380, $top + 132, 52, 16)
                    }
                    finally {
                        $ghostPen.Dispose()
                        $exposedPadBrush.Dispose()
                        $emptyFootprintBrush.Dispose()
                    }

                    Draw-Resistor $Graphics (New-Rect ($left + 390) ($top + 184) 66 20) $Brightness $ColorShift
                }
                "ForeignObject" {
                    $objectBrush = New-Brush 86 42 30 $Brightness 0 235
                    $objectPen = New-Pen 132 92 55 3 $Brightness 0 240
                    try {
                        $points = [System.Drawing.Point[]]@(
                            [System.Drawing.Point]::new($left + 448, $top + 180),
                            [System.Drawing.Point]::new($left + 506, $top + 170),
                            [System.Drawing.Point]::new($left + 530, $top + 210),
                            [System.Drawing.Point]::new($left + 482, $top + 232),
                            [System.Drawing.Point]::new($left + 436, $top + 210)
                        )
                        $Graphics.FillPolygon($objectBrush, $points)
                        $Graphics.DrawPolygon($objectPen, $points)
                    }
                    finally {
                        $objectBrush.Dispose()
                        $objectPen.Dispose()
                    }
                }
                "PolarityMismatch" {
                    Draw-Capacitor $Graphics (New-Rect ($left + 446) ($top + 236) 44 30) $Brightness $ColorShift $true
                    $markPen = New-Pen 246 246 236 7 $Brightness 0
                    $darkBrush = New-Brush 12 18 18 $Brightness 0 230
                    $lightBrush = New-Brush 246 246 236 $Brightness 0 245
                    try {
                        $Graphics.FillEllipse($darkBrush, $left + 432, $top + 224, 26, 26)
                        $Graphics.FillEllipse($lightBrush, $left + 438, $top + 230, 14, 14)
                        $Graphics.DrawLine($markPen, $left + 428, $top + 220, $left + 506, $top + 282)
                        $Graphics.DrawLine($markPen, $left + 506, $top + 220, $left + 428, $top + 282)
                    }
                    finally {
                        $markPen.Dispose()
                        $darkBrush.Dispose()
                        $lightBrush.Dispose()
                    }
                }
            }

            Add-VisualNoise $Graphics $Random $width $height $NoiseLevel
        }
        finally {
            $surfaceBrush.Dispose()
            $boardBrush.Dispose()
            $boardAltBrush.Dispose()
            $boardPen.Dispose()
            $gridPen.Dispose()
            $tracePen.Dispose()
            $thinTracePen.Dispose()
            $silkPen.Dispose()
            $padBrush.Dispose()
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $font.Dispose()
        $smallFont.Dispose()
        $labelFont.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function New-Variation {
    param(
        [System.Random]$Random,
        [int]$BrightnessRange,
        [int]$ShiftRange,
        [int]$NoiseMin,
        [int]$NoiseMax,
        [int]$ColorRange
    )

    return @{
        Brightness = $Random.Next(-$BrightnessRange, $BrightnessRange + 1)
        ShiftX = $Random.Next(-$ShiftRange, $ShiftRange + 1)
        ShiftY = $Random.Next(-$ShiftRange, $ShiftRange + 1)
        NoiseLevel = $Random.Next($NoiseMin, $NoiseMax + 1)
        ColorShift = $Random.Next(-$ColorRange, $ColorRange + 1)
    }
}

function Add-TruthRow {
    param(
        [System.Collections.Generic.List[string]]$Rows,
        [string]$Image,
        [string]$Truth,
        [string]$Notes
    )

    $Rows.Add(("{0},{1},{2}" -f $Image, $Truth, $Notes))
}

$random = [System.Random]::new($Seed)
$truthRows = [System.Collections.Generic.List[string]]::new()
$truthRows.Add("image,truth,notes")
$anomalies = @("SolderBridge", "MissingComponent", "ShiftedComponent", "ForeignObject", "PolarityMismatch")

for ($i = 1; $i -le $GoldenCount; $i++) {
    $path = Join-Path $roleFolders.golden ("synthetic_golden_{0:000}.png" -f $i)
    $variation = New-Variation $random 2 1 1 2 1
    Write-BoardImage $path $random $i "Golden Reference" "None" $variation.Brightness $variation.ShiftX $variation.ShiftY $variation.NoiseLevel $variation.ColorShift
}

for ($i = 1; $i -le $OkLearningCount; $i++) {
    $path = Join-Path $roleFolders.ok_learning ("synthetic_ok_learning_{0:000}.png" -f $i)
    $variation = New-Variation $random 12 2 2 5 4
    Write-BoardImage $path $random $i "OK Learning" "None" $variation.Brightness $variation.ShiftX $variation.ShiftY $variation.NoiseLevel $variation.ColorShift
}

for ($i = 1; $i -le $OkValidationCount; $i++) {
    $fileName = "synthetic_ok_validation_{0:000}.png" -f $i
    $path = Join-Path $roleFolders.ok_validation $fileName
    $variation = New-Variation $random 28 1 1 3 2
    Write-BoardImage $path $random $i "OK Validation" "None" $variation.Brightness $variation.ShiftX $variation.ShiftY $variation.NoiseLevel $variation.ColorShift
    Add-TruthRow $truthRows ("ok_validation/{0}" -f $fileName) "OK" "Image-level OK truth for false-call metrics only"
}

for ($i = 1; $i -le $InspectionCount; $i++) {
    $fileName = "synthetic_inspection_{0:000}.png" -f $i
    $path = Join-Path $roleFolders.inspection $fileName
    $variation = New-Variation $random 10 2 2 5 4
    $anomaly = $anomalies[($i - 1) % $anomalies.Count]
    Write-BoardImage $path $random $i "Inspection" $anomaly $variation.Brightness $variation.ShiftX $variation.ShiftY $variation.NoiseLevel $variation.ColorShift
    Add-TruthRow $truthRows ("inspection/{0}" -f $fileName) "UNKNOWN" "Inspection sample with synthetic abnormality not used for learning labels"
}

for ($i = 1; $i -le $NgValidationCount; $i++) {
    $fileName = "synthetic_ng_validation_{0:000}.png" -f $i
    $path = Join-Path $roleFolders.ng_validation $fileName
    $variation = New-Variation $random 10 2 2 5 4
    $anomaly = $anomalies[($i - 1) % $anomalies.Count]
    Write-BoardImage $path $random $i "NG Validation" $anomaly $variation.Brightness $variation.ShiftX $variation.ShiftY $variation.NoiseLevel $variation.ColorShift
    Add-TruthRow $truthRows ("ng_validation/{0}" -f $fileName) "NG" "Image-level NG truth for possible-escape metrics only"
}

$readmePath = Join-Path $OutputRoot "README_SYNTHETIC_IMAGE_LEARNING_DEMO.txt"
@"
Synthetic Image-only PCB Learning Demo Project

Synthetic demo only. This proves workflow capability, not customer acceptance.

Folders:
- golden: best-known synthetic reference boards.
- ok_learning: good synthetic boards used to learn normal visual variation.
- ok_validation: good synthetic boards used to calibrate false calls.
- inspection: synthetic samples to inspect after learning.
- ng_validation: known-bad synthetic samples used only to estimate possible escapes.

Important:
- This generator creates images only.
- It does not create per-defect training labels.
- It does not create bounding-box labels.
- image_truth.csv is image-level truth for reporting metrics only.
- Image-only Stage 1 learning is not live camera validation.
- Formal acceptance requires customer/evaluator data and reviewer signoff.

Recommended command:
dotnet run --project AOI_Monitor.Tools -- client-image-learning-demo --project-folder "$OutputRoot" --output "TestResults/image-learning-demo-output" --operator ci-demo --false-call-target 0.05
"@ | Set-Content -LiteralPath $readmePath -Encoding UTF8

$truthPath = Join-Path $OutputRoot "image_truth.csv"
$truthRows | Set-Content -LiteralPath $truthPath -Encoding UTF8

Write-Output "Synthetic image-only learning demo project generated."
Write-Output "OutputRoot: $OutputRoot"
Write-Output "Golden: $GoldenCount"
Write-Output "OK Learning: $OkLearningCount"
Write-Output "OK Validation: $OkValidationCount"
Write-Output "Inspection: $InspectionCount"
Write-Output "NG Validation: $NgValidationCount"
Write-Output "Truth CSV: $truthPath"
Write-Output "README: $readmePath"
