$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$samples = @(
    @{ File = 'zh-hans-event.png'; Font = 'Microsoft YaHei UI'; Line1 = '项目评审会议'; Line2 = '7月20日 14:30 会议室A'; Rtl = $false },
    @{ File = 'zh-hant-event.png'; Font = 'Microsoft JhengHei UI'; Line1 = '專案評審會議'; Line2 = '7月20日 14:30 會議室A'; Rtl = $false },
    @{ File = 'en-event.png'; Font = 'Segoe UI'; Line1 = 'Project review meeting'; Line2 = 'July 20 2:30 PM Room A'; Rtl = $false },
    @{ File = 'vi-latin-extended.png'; Font = 'Segoe UI'; Line1 = 'Cuộc họp đánh giá dự án'; Line2 = '20 tháng 7 14:30 Phòng A'; Rtl = $false },
    @{ File = 'ja-event.png'; Font = 'Yu Gothic UI'; Line1 = 'プロジェクトレビュー会議'; Line2 = '7月20日 14:30 会議室A'; Rtl = $false },
    @{ File = 'ar-unsupported.png'; Font = 'Segoe UI'; Line1 = 'اجتماع مراجعة المشروع'; Line2 = '20 يوليو 14:30 الغرفة أ'; Rtl = $true },
    @{ File = 'th-unsupported-no-spaces.png'; Font = 'Leelawadee UI'; Line1 = 'ประชุมทบทวนโครงการ'; Line2 = '20 กรกฎาคม 14:30 ห้อง A'; Rtl = $false }
)

$outputDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
foreach ($sample in $samples) {
    $bitmap = [System.Drawing.Bitmap]::new(1200, 360, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $titleFont = [System.Drawing.Font]::new($sample.Font, 46, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $bodyFont = [System.Drawing.Font]::new($sample.Font, 34, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $format = [System.Drawing.StringFormat]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        if ($sample.Rtl) {
            $format.FormatFlags = [System.Drawing.StringFormatFlags]::DirectionRightToLeft
            $format.Alignment = [System.Drawing.StringAlignment]::Far
        }
        $graphics.DrawString($sample.Line1, $titleFont, [System.Drawing.Brushes]::Black, [System.Drawing.RectangleF]::new(70, 75, 1060, 80), $format)
        $graphics.DrawString($sample.Line2, $bodyFont, [System.Drawing.Brushes]::Black, [System.Drawing.RectangleF]::new(70, 190, 1060, 70), $format)
        $bitmap.Save((Join-Path $outputDirectory $sample.File), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $format.Dispose()
        $bodyFont.Dispose()
        $titleFont.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}
