# Translating Sea Power Tactical UI

## English

1. **Where the tables are.** `BepInEx/plugins/SeaPowerTacticalUI/lang/language_<code>/*.ini`. The mod reads **all** `*.ini` files in the folder, in file name order. The code is the part of the game language name before `_` or `-` (`ru_RU` becomes `ru`).
2. **Add a language.** Copy `language_en` to `language_xx` and translate only the values. Restart the game or change the language in the game Options. No rebuild is needed. The setting `[Ui] Language=auto` follows the game; set a code to force a language.
3. **File rules.**
   - Sections: `[MapLayers]` (map menu and layers), `[TacticalDoctrine]` (Standing Orders names), `[Tooltip]` (Standing Orders hints), `[SeaPowerTacticalUI]` (windows, XO menu, ring, messages).
   - Do not change or translate keys; key case does not matter.
   - Keep `{0}`, `{1}` placeholders (their order in the sentence may change).
   - `\n` is a line break; lines starting with `;` or `#` are comments.
   - If a key repeats, the first value wins.
4. **Where text comes from.** First the game's own tables (`getText`), then your table, then the English table, then the key id. The game's answer `MISSING TEXT - [...]` counts as no text. A key missing from your file falls back to English, so a partial translation works.
5. **Length.** The windows are narrow; long captions are cut. Check the result in the game.
6. **Not translated.** Voice phrases (`say_ru`) and names of already created groups.
7. **Settings window.** `settings.ini` holds the captions and hints of the Settings window (section titles, key captions and hints, buttons). A key without a caption shows its config name.
8. **Intercept course (T-138).** The contact menu entry, the map label and the refusal reasons use the keys `icpt.menu_*`, `icpt.line_*`, `icpt.land_hint` and `icpt.reason.*` in `sea_power_tactical_ui.ini`; the settings captions use `settings.intercept.*` in `settings.ini`. Keep `{0}`..`{3}`; a missing reason key shows the reason code.
9. **Orders field (T-234).** `unit_action.ini` holds the labels of the "Orders:" field of a unit: `ua.cmd.*` (the command) and `ua.ph.*` (the phase); the label is "command: phase". Keep `{0}`, `{1}`; the group name is never shown.
10. **For developers.** `uv run pytest tests/test_sea_power_ui_text.py tests/test_sea_power_ui_text_keys.py -q` checks key and `{n}` parity between ru and en.
11. **Chinese (T-338).** `language_cn` (the game names Chinese `cn`) is a machine translation. Corrections are welcome: send the changed lines in the Workshop Discussions tab.

## Русский

Как добавить язык: скопируйте папку `lang/language_en` в `lang/language_xx` (xx — код языка игры до `_`), переведите значения, перезапустите игру. Ключи, регистр ключей и `{0}`, `{1}` не меняйте. Мод читает все `*.ini` в папке. Нет ключа в вашем файле — будет английский текст. Язык задаёт `[Ui] Language` (`auto` — как в игре). Голосовые фразы и имена уже созданных групп не переводятся.

Файл `settings.ini` (окно «Настройки», T-144): названия разделов, подписи и подсказки ключей, кнопки. Нет подписи ключа — в окне показывается имя ключа из конфига.

Перехват (T-138): пункт меню контакта, подпись линии и причины отказа - ключи `icpt.menu_*`, `icpt.line_*`, `icpt.land_hint`, `icpt.reason.*` в `sea_power_tactical_ui.ini`; подписи настроек - `settings.intercept.*` в `settings.ini`. `{0}`..`{3}` не меняйте; нет ключа причины - показывается код причины.

Поле «Задача:» (T-234): `unit_action.ini` - метки поля юнита, `ua.cmd.*` (команда) и `ua.ph.*` (фаза); метка = «команда: фаза». `{0}`, `{1}` не меняйте; имя группы в метке не показывается.

Китайский (T-338): `language_cn` (в игре китайский — `cn`) — машинный перевод. Правки присылайте во вкладке Discussions предмета Мастерской.
