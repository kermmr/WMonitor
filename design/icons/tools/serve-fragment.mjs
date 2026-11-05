import http from "node:http";
import fs from "node:fs";

const [, , file, port = "8397"] = process.argv;
if (!file) {
  console.error("usage: node serve-fragment.mjs <file> [port]");
  process.exit(1);
}

http
  .createServer((req, res) => {
    if (req.url !== "/") {
      res.writeHead(404);
      res.end();
      return;
    }
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    fs.createReadStream(file).pipe(res);
  })
  .listen(Number(port), "127.0.0.1", () => console.log(`serving on http://127.0.0.1:${port}/`));
