// Converte os scriptlets do uBlock Origin (src/js/resources/scriptlets.js) para recursos do adblock-rust.
// Uso: node build-scriptlets.mjs <pasta do código do uBlock Origin> <ficheiro de saída .json>
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [, , uboDir, outFile] = process.argv;
if (!uboDir || !outFile) {
    console.error('uso: node build-scriptlets.mjs <uBlock> <saida.json>');
    process.exit(2);
}

const entry = path.join(uboDir, 'src', 'js', 'resources', 'scriptlets.js');
const { builtinScriptlets } = await import(pathToFileURL(entry).href);

const resources = [];
let skipped = 0;
for (const scriptlet of builtinScriptlets) {
    // "Trusted" só pode ser usado por listas de confiança. Os do mundo isolado correm no mundo da página:
    // funcionam igual, só ficam visíveis aos scripts da página.
    if (scriptlet.requiresTrust) {
        skipped++;
        continue;
    }
    resources.push({
        name: scriptlet.name,
        aliases: scriptlet.aliases ?? [],
        // No adblock-rust só "application/javascript" é invocável por +js(...); "fn/javascript" serve para as dependências.
        kind: { mime: scriptlet.name.endsWith('.fn') ? 'fn/javascript' : 'application/javascript' },
        content: Buffer.from(scriptlet.fn.toString()).toString('base64'),
        dependencies: scriptlet.dependencies ?? [],
    });
}

fs.writeFileSync(outFile, JSON.stringify(resources));
console.log(`${resources.length} scriptlets escritos em ${outFile} (${skipped} "trusted" ignorados)`);
