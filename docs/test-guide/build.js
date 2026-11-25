// Dựng trang kịch bản test từ guide.json (nguồn) + template.html.
//   node docs/test-guide/build.js              → remi-test-guide.html (mở thẳng bằng trình duyệt)
//   node docs/test-guide/build.js --artifact   → bản không có khung <html> để đăng lên artifact claude.ai
// Sửa/thêm bước test: sửa guide.json (giữ nguyên id bước cũ để kết quả đã tích không mất), rồi chạy lại lệnh trên.
const fs = require("fs");
const path = require("path");
const dir = __dirname;
const artifact = process.argv.includes("--artifact");
const data = JSON.parse(fs.readFileSync(path.join(dir, "guide.json"), "utf8"));
const ids = new Set();
let cases = 0;
for (const g of data.groups || []) {
  for (const c of g.cases || []) {
    cases++;
    if (ids.has(c.id)) throw new Error("Trùng id bước: " + c.id);
    ids.add(c.id);
    if (!c.title || !(c.steps || []).length || !(c.expect || []).length) throw new Error("Bước thiếu nội dung: " + c.id);
  }
}
const json = JSON.stringify(data).replace(/</g, "\u003c");
let html = fs.readFileSync(path.join(dir, "template.html"), "utf8").replace("/*GUIDE_DATA*/null", json);
if (!artifact) {
  html = `<!doctype html>\n<html lang="vi">\n<head>\n<meta charset="utf-8">\n<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">\n<style>[hidden]{display:none!important}body{margin:0}img{max-width:100%}</style>\n</head>\n<body>\n${html}\n</body>\n</html>\n`;
}
const out = path.join(dir, artifact ? "remi-test-guide.artifact.html" : "remi-test-guide.html");
fs.writeFileSync(out, html);
console.log(`${data.groups.length} nhóm, ${cases} bước → ${path.basename(out)}`);
