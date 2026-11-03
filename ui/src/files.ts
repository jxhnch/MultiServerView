/** Liest eine Textdatei; erkennt UTF-16 (z. B. .reg-Exporte von Windows) anhand der Markierung am Dateianfang. */
export async function readTextAuto(file: File): Promise<string> {
  const buf = await file.arrayBuffer();
  const b = new Uint8Array(buf);
  if (b[0] === 0xff && b[1] === 0xfe) return new TextDecoder('utf-16le').decode(buf);
  if (b[0] === 0xfe && b[1] === 0xff) return new TextDecoder('utf-16be').decode(buf);
  return new TextDecoder('utf-8').decode(buf);
}
