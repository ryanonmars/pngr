// Build-time artwork for Finder's drag-to-Applications installation window.
import AppKit

let width = 720, height = 460
let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width * 2,
    pixelsHigh: height * 2, bitsPerSample: 8, samplesPerPixel: 4,
    hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
    bytesPerRow: 0, bitsPerPixel: 0)!
bitmap.size = NSSize(width: width, height: height)
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
let background = NSColor(calibratedRed: 0.09, green: 0.10, blue: 0.11, alpha: 1)
background.setFill()
NSBezierPath(rect: NSRect(x: 0, y: 0, width: width, height: height)).fill()
// Finder uses dark icon captions for picture backgrounds, even in dark mode.
// Small neutral caption plates keep its native labels readable on the dark art.
NSColor(white: 0.72, alpha: 1).setFill()
for x in [112, 452] {
    NSBezierPath(roundedRect: NSRect(x: x, y: 139, width: 156, height: 35),
                 xRadius: 7, yRadius: 7).fill()
}
func text(_ value: String, y: CGFloat, size: CGFloat, color: NSColor, weight: NSFont.Weight = .regular) {
    let paragraph = NSMutableParagraphStyle()
    paragraph.alignment = .center
    (value as NSString).draw(in: NSRect(x: 30, y: y, width: 660, height: 60),
        withAttributes: [.font: NSFont.systemFont(ofSize: size, weight: weight),
                         .foregroundColor: color, .paragraphStyle: paragraph])
}
guard CommandLine.arguments.count == 3,
      let wordmark = NSImage(contentsOfFile: CommandLine.arguments[2]) else {
    fatalError("Installation artwork requires the Pngr wordmark.")
}
let wordmarkWidth: CGFloat = 200
let wordmarkHeight = wordmarkWidth * wordmark.size.height / wordmark.size.width
wordmark.draw(in: NSRect(x: (CGFloat(width) - wordmarkWidth) / 2,
                         y: 333, width: wordmarkWidth, height: wordmarkHeight))
text("Network diagnostics, clearly.", y: 270, size: 15, color: NSColor(white: 0.67, alpha: 1))
let arrow = NSBezierPath()
arrow.move(to: NSPoint(x: 313, y: 230)); arrow.line(to: NSPoint(x: 407, y: 230))
arrow.move(to: NSPoint(x: 388, y: 249)); arrow.line(to: NSPoint(x: 407, y: 230))
arrow.line(to: NSPoint(x: 388, y: 211))
arrow.lineWidth = 3
NSColor(calibratedRed: 0.63, green: 0.74, blue: 0.63, alpha: 1).setStroke()
arrow.stroke()
text("Drag Pngr to Applications", y: 73, size: 19, color: .white, weight: .medium)
text("Then open Pngr from your Applications folder.", y: 41, size: 13, color: NSColor(white: 0.65, alpha: 1))
NSGraphicsContext.restoreGraphicsState()
try bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
