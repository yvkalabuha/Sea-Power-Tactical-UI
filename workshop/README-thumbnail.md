# thumbnail.jpg

The thumbnail is final: a drawn picture (sonar sight + "Sea Power / Tactical UI"), made by `make_thumbnail.sh`.
The customer chose it on 2026-10-26 instead of an in-game frame: a dense map frame is unreadable at the Workshop list size.
In-game frames go to the item gallery (`screenshots/`), not to the thumbnail.

To rebuild: `./make_thumbnail.sh` (needs ImageMagick `convert`).

Rules (checked by `workshop_check.py`): JPEG, exactly 512x512, at most 1 MiB.
