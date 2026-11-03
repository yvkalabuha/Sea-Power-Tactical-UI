#!/usr/bin/env bash
# T-105/T-249: Workshop thumbnail (512x512 JPEG, <= 1 MB) drawn from scratch, no game imagery.
# Final choice of the customer (2026-10-26): this drawing, not an in-game frame (unreadable at Workshop list size).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/thumbnail.jpg}"
convert -size 512x512 gradient:'#0b2a44-#04101c' \
  -fill none -stroke '#2f7fb8' -strokewidth 3 -draw 'circle 256,230 256,330' -draw 'circle 256,230 256,250' \
  -stroke '#2f7fb8' -strokewidth 2 -draw 'line 256,100 256,360' -draw 'line 126,230 386,230' \
  -stroke none -fill '#e8f2fa' -gravity south -pointsize 40 -annotate +0+90 'Sea Power' \
  -pointsize 52 -annotate +0+30 'Tactical UI' \
  -strip -sampling-factor 4:2:0 -quality 88 "$OUT"
echo "wrote $OUT"
