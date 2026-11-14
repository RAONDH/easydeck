import AppKit

// 앱 아이콘(iconset) 생성: swift Tools/make_icon.swift <출력폴더>
let out = CommandLine.arguments[1]
let sizes: [(Int, String)] = [(16, "16x16"), (32, "16x16@2x"), (32, "32x32"), (64, "32x32@2x"),
                              (128, "128x128"), (256, "128x128@2x"), (256, "256x256"), (512, "256x256@2x"),
                              (512, "512x512"), (1024, "512x512@2x")]
let colors = ["#3B5BDB", "#C2255C", "#E8590C", "#2B8A3E", "#5F3DC4", "#0B7285"]
func hex(_ s: String) -> NSColor {
    var v: UInt64 = 0; Scanner(string: String(s.dropFirst())).scanHexInt64(&v)
    return NSColor(srgbRed: CGFloat(v >> 16 & 255) / 255, green: CGFloat(v >> 8 & 255) / 255, blue: CGFloat(v & 255) / 255, alpha: 1)
}
for (px, name) in sizes {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let s = CGFloat(px)
    let body = NSRect(x: s * 0.1, y: s * 0.1, width: s * 0.8, height: s * 0.8)
    NSGradient(starting: hex("#3A3F4B"), ending: hex("#1C1F26"))!
        .draw(in: NSBezierPath(roundedRect: body, xRadius: s * 0.18, yRadius: s * 0.18), angle: -90)
    let cell = s * 0.2, gap = s * 0.04
    let ox = body.midX - (cell * 3 + gap * 2) / 2, oy = body.midY - (cell * 2 + gap) / 2
    for i in 0..<6 {
        let r = NSRect(x: ox + CGFloat(i % 3) * (cell + gap), y: oy + CGFloat(i / 3) * (cell + gap), width: cell, height: cell)
        hex(colors[i]).setFill()
        NSBezierPath(roundedRect: r, xRadius: cell * 0.22, yRadius: cell * 0.22).fill()
    }
    NSGraphicsContext.restoreGraphicsState()
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "\(out)/icon_\(name).png"))
}
