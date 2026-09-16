export type Feature = "natural" | "analysis" | "code-block" | "repositories" | "llm";
export type FeatureMessages = Partial<Record<Feature, string>>;

// A response belongs to the feature and attempt that started it, never the tab
// that happens to be visible when the response arrives.
export function createFeatureErrors(changed: (messages: FeatureMessages) => void) {
  let messages: FeatureMessages = {};
  const attempts: Partial<Record<Feature, number>> = {};
  const report = (feature: Feature, message: string) => {
    messages = { ...messages, [feature]: message };
    changed(messages);
  };
  const begin = (feature: Feature) => {
    const attempt = (attempts[feature] ?? 0) + 1;
    attempts[feature] = attempt;
    report(feature, "");
    return (message: string) => { if (attempts[feature] === attempt) report(feature, message); };
  };
  return { begin, clear: (feature: Feature) => { begin(feature); },
    reporters: Object.fromEntries((["natural", "analysis", "code-block", "repositories", "llm"] as Feature[])
      .map(feature => [feature, (message: string) => report(feature, message)])) as Record<Feature, (message: string) => void> };
}
