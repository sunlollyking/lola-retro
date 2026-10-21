// Prints an HTML file to an A4 PDF with macOS's own WebKit (no other tools needed).
// Usage: swift tools/html_to_pdf.swift IN.html OUT.pdf
import AppKit
import WebKit

let args = CommandLine.arguments
guard args.count == 3 else {
    FileHandle.standardError.write("usage: html_to_pdf.swift IN.html OUT.pdf\n".data(using: .utf8)!)
    exit(2)
}
let input = URL(fileURLWithPath: args[1])
let output = URL(fileURLWithPath: args[2])

final class Printer: NSObject, WKNavigationDelegate {
    let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 1100), styleMask: [.borderless],
                          backing: .buffered, defer: false)
    let web = WKWebView(frame: NSRect(x: 0, y: 0, width: 800, height: 1100))

    func start() {
        window.contentView = web
        web.navigationDelegate = self
        web.loadFileURL(input, allowingReadAccessTo: input.deletingLastPathComponent().deletingLastPathComponent()
                                                         .deletingLastPathComponent())
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        // the pictures are loaded with the page; a moment for layout
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { self.print() }
    }

    func print() {
        let info = NSPrintInfo(dictionary: [.jobSavingURL: output])
        info.jobDisposition = .save
        info.paperSize = NSSize(width: 595.28, height: 841.89)      // A4 in points
        for m in [\NSPrintInfo.topMargin, \NSPrintInfo.bottomMargin] { info[keyPath: m] = 48 }
        for m in [\NSPrintInfo.leftMargin, \NSPrintInfo.rightMargin] { info[keyPath: m] = 44 }
        info.horizontalPagination = .fit
        info.verticalPagination = .automatic
        info.isVerticallyCentered = false
        info.isHorizontallyCentered = false
        let op = web.printOperation(with: info)
        op.showsPrintPanel = false
        op.showsProgressPanel = false
        op.view?.frame = web.bounds
        op.runModal(for: window, delegate: self, didRun: #selector(done(_:success:context:)), contextInfo: nil)
    }

    @objc func done(_ op: NSPrintOperation, success: Bool, context: UnsafeMutableRawPointer?) {
        Swift.print(success ? "wrote \(output.path)" : "printing failed")
        exit(success ? 0 : 1)
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
let printer = Printer()
printer.start()
app.run()
