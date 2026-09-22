export async function readNaturalTestStream<T>(body: ReadableStream<Uint8Array>, receive: (event: T) => void) {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let pending = "";
  try {
    for (;;) {
      const chunk = await reader.read();
      pending += decoder.decode(chunk.value, { stream: !chunk.done });
      const lines = pending.split("\n");
      pending = lines.pop()!;
      for (const line of lines) if (line.trim()) receive(JSON.parse(line) as T);
      if (chunk.done) {
        if (pending.trim()) receive(JSON.parse(pending) as T);
        break;
      }
    }
  } finally { reader.releaseLock(); }
}
