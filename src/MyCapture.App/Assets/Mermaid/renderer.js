"use strict";
// Only this fixed script may run. Source enters through a JSON string argument; it is never
// evaluated as JavaScript. No diagram bindFunctions or click handlers are attached.
window.renderDiagram = async (source, id) => {
    const container = document.getElementById("diagram");
    container.replaceChildren();
    try {
        mermaid.initialize({
            startOnLoad: false, securityLevel: "strict", theme: "default",
            fontFamily: '"Segoe UI", "Malgun Gothic", sans-serif',
            maxTextSize: 50000, maxEdges: 500, suppressErrorRendering: true,
            htmlLabels: false, flowchart: { htmlLabels: false, useMaxWidth: false },
            sequence: { useMaxWidth: false }, class: { useMaxWidth: false },
            state: { useMaxWidth: false }, er: { useMaxWidth: false },
            secure: ["securityLevel", "startOnLoad", "maxTextSize", "maxEdges", "suppressErrorRendering",
                "secure", "themeCSS", "fontFamily", "htmlLabels", "flowchart"]
        });
        const result = await mermaid.render("diagram-" + id, source);
        container.innerHTML = result.svg;
        const svg = container.querySelector("svg");
        if (!svg) throw new Error("No diagram");
        // Strip inactive link targets too; the bitmap is the only output of this host.
        svg.querySelectorAll("a").forEach(a => { a.removeAttribute("href"); a.removeAttribute("xlink:href"); });
        const box = svg.viewBox.baseVal;
        const width = Math.ceil(box.width || svg.getBoundingClientRect().width);
        const height = Math.ceil(box.height || svg.getBoundingClientRect().height);
        if (!(width > 0 && height > 0 && width <= 4000 && height <= 4000)) throw new Error("Diagram bounds");
        svg.setAttribute("width", String(width));
        svg.setAttribute("height", String(height));
        document.body.style.width = (width + 24) + "px";
        await document.fonts.ready;
        await Promise.race([
            new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))),
            new Promise(resolve => setTimeout(resolve, 100))
        ]);
        chrome.webview.postMessage({ id, ok: true, width: width + 24, height: height + 24 });
    } catch (_) {
        container.replaceChildren();
        chrome.webview.postMessage({ id, ok: false });
    }
};
