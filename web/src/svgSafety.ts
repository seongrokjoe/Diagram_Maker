// Preserve Mermaid's static presentation without allowing CSS or SVG to fetch or
// execute anything. A rejected declaration must not erase unrelated safe rules.
const properties = new Set(("fill fill-opacity fill-rule stroke stroke-width stroke-opacity stroke-dasharray stroke-dashoffset stroke-linecap stroke-linejoin stroke-miterlimit " +
  "color background background-color opacity font font-family font-size font-weight font-style text-anchor text-decoration dominant-baseline alignment-baseline " +
  "white-space word-spacing letter-spacing line-height paint-order vector-effect visibility display overflow cursor width height max-width min-width " +
  "rx ry marker-start marker-mid marker-end").split(" "));

function declarations(style: CSSStyleDeclaration, ids: Set<string>): string {
  const safe: string[] = [];
  for (const name of Array.from(style)) {
    const value = style.getPropertyValue(name).trim();
    if (!properties.has(name) || /[\\@<>]|(?:expression|var|attr|image-set)\s*\(|(?:javascript|data|https?):/i.test(value)) continue;
    const references = [...value.matchAll(/url\(\s*['"]?#([A-Za-z0-9_.:-]+)['"]?\s*\)/gi)];
    if (/url\s*\(/i.test(value) && (references.length !== (value.match(/url\s*\(/gi)?.length ?? 0) || references.some(r => !ids.has(r[1])))) continue;
    safe.push(`${name}:${value}`);
  }
  return safe.join(";");
}

export function sanitizeSvg(svg: string, displayMarker = ""): string {
  const document = new DOMParser().parseFromString(svg, "image/svg+xml");
  const root = document.documentElement;
  if (root.localName !== "svg" || document.querySelector("parsererror")) throw new Error("Invalid SVG document.");
  const tags = new Set("svg g path rect polygon polyline line circle ellipse text tspan title desc defs symbol marker linearGradient radialGradient stop clipPath mask style".split(" "));
  document.querySelectorAll("script, foreignObject, iframe, object, embed, image, animate, animateMotion, animateTransform, set, use").forEach(e => e.remove());
  document.querySelectorAll("a").forEach(e => e.replaceWith(...e.childNodes));
  document.querySelectorAll("*").forEach(e => { if (e.namespaceURI !== "http://www.w3.org/2000/svg" || !tags.has(e.localName)) e.remove(); });
  const rootId = root.id;
  const ids = new Set([...root.querySelectorAll("[id]")].map(e => e.id).concat(rootId));
  document.querySelectorAll("style").forEach(element => {
    const sheet = new CSSStyleSheet();
    // Constructed stylesheets do not load @import. No parsed sheet is attached.
    try { sheet.replaceSync(element.textContent ?? ""); } catch { element.remove(); return; }
    const rules: string[] = [];
    for (const rule of Array.from(sheet.cssRules)) {
      if (rule.type !== CSSRule.STYLE_RULE || !/^[A-Za-z][A-Za-z0-9_-]*$/.test(rootId)) continue;
      const entry = rule as CSSStyleRule;
      // Reject escapes and at-rules, including keyframes, rule by rule. Every
      // selector must stay inside this SVG; :root/body cannot target the page.
      const selectors = entry.selectorText.split(",").map(s => s.trim());
      if (selectors.some(s => /[\\@+~]|:has\(|:is\(|:where\(/i.test(s) ||
        !(s === `#${rootId}` || s.startsWith(`#${rootId} `) || s.startsWith(`#${rootId}>`)))) continue;
      const value = declarations(entry.style, ids);
      if (value) rules.push(`${selectors.join(",")}{${value}}`);
    }
    element.textContent = rules.join("\n");
  });
  for (const element of Array.from(document.querySelectorAll("*"))) {
    for (const attribute of Array.from(element.attributes)) {
      const name = attribute.name.toLowerCase(); const value = attribute.value.trim();
      if (name === "style") {
        const parsed = window.document.createElement("span").style;
        parsed.cssText = value;
        element.setAttribute("style", declarations(parsed, ids));
      } else if (name.startsWith("on") || name === "href" || name === "xlink:href" || /javascript:|[\\@]/i.test(value) ||
        /url\s*\(/i.test(value) && !/^url\(\s*['"]?#[A-Za-z0-9_.:-]+['"]?\s*\)$/i.test(value)) element.removeAttribute(attribute.name);
      else if (/url\s*\(/i.test(value) && !ids.has(value.match(/#([A-Za-z0-9_.:-]+)/)?.[1] ?? "")) element.removeAttribute(attribute.name);
    }
  }
  if (displayMarker) {
    const text = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    while (text.nextNode()) text.currentNode.nodeValue = text.currentNode.nodeValue?.split(displayMarker).join("") ?? "";
  }
  return new XMLSerializer().serializeToString(root);
}
