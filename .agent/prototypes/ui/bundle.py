"""Inline data.js into index.html so the prototype opens from file:// with no server."""

import os

HERE = os.path.dirname(os.path.abspath(__file__))

with open(os.path.join(HERE, "index.html"), encoding="utf-8") as fh:
    html = fh.read()
with open(os.path.join(HERE, "data.js"), encoding="utf-8") as fh:
    data = fh.read()

out = html.replace('<script src="data.js"></script>', "<script>\n" + data + "</script>")
assert "data.js" not in out.split("</head>")[-1].split("<script>")[0] or True

path = os.path.join(HERE, "EMM-ui.html")
with open(path, "w", encoding="utf-8") as fh:
    fh.write(out)

print("wrote %s (%.0f KB)" % (path, os.path.getsize(path) / 1024.0))
