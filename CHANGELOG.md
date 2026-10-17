# Changelog

## [4.0.0](https://github.com/pashamed/dtek-monitor/compare/v3.0.0...v4.0.0) (2026-10-17)


### ⚠ BREAKING CHANGES

* the PAT secret is no longer used. Remove it from the repository secrets and revoke the token.
* bot state moved from the artifacts/ folder in main to the artifacts branch. The branch is created on the first run with an outage, which sends a new message. Bot commits "chore: update artifacts" no longer land in main.
* when the outage time or reason changes during the day, the bot sends a new message (with a notification) as a reply to the previous one instead of editing a single message per day.

### Features

* add repository_dispatch support (as main trigger) ([f7bed73](https://github.com/pashamed/dtek-monitor/commit/f7bed73bb6dd585f5c6e3b6645adaab2fd335562))
* add scheduled outage check ([a33a209](https://github.com/pashamed/dtek-monitor/commit/a33a20928c355310968ac2ffc2c1c4d917641a0e))
* add support for different DTEK regions ([7092638](https://github.com/pashamed/dtek-monitor/commit/70926382f2b34a3d316ade671186e174cca8864e))
* change message body, save message id to further editing ([9632340](https://github.com/pashamed/dtek-monitor/commit/963234087288ee68d2480635bfc8ba1fdf9f5b0b))
* clean yesterday last message ([9632340](https://github.com/pashamed/dtek-monitor/commit/963234087288ee68d2480635bfc8ba1fdf9f5b0b))
* create monitor.js with main behavior ([9632340](https://github.com/pashamed/dtek-monitor/commit/963234087288ee68d2480635bfc8ba1fdf9f5b0b))
* extend silent night mode to 22:00-08:00 ([217d839](https://github.com/pashamed/dtek-monitor/commit/217d83989dbf88eb13b947d81a27e3852196fb76))
* full rewrite of main script ([9632340](https://github.com/pashamed/dtek-monitor/commit/963234087288ee68d2480635bfc8ba1fdf9f5b0b))
* provide concurrency for run one action at the same time ([83b1238](https://github.com/pashamed/dtek-monitor/commit/83b12381c8abfcc12a2fd4d2f71c2331376d1aa5))
* regional emergency popup alerts, grid status and on-demand emergency checks ([21f248f](https://github.com/pashamed/dtek-monitor/commit/21f248f7de95de3081b4889e0a0b91b5a1fa36e1))
* replace timestamps emoji and show dates before times ([7efae33](https://github.com/pashamed/dtek-monitor/commit/7efae33aca9e6f9c97826e4a310700bc329183be))
* reply with a new message when the outage changes ([2313fb4](https://github.com/pashamed/dtek-monitor/commit/2313fb4d1a9ece8b34dcd5a0389a283001479904))
* retry getting info and sending notification ([4d7bea4](https://github.com/pashamed/dtek-monitor/commit/4d7bea48180738196f762208819d86fba77ee1ec))
* rewrite to use playwright to emulate real browser with headers, cookies and csrf ([9632340](https://github.com/pashamed/dtek-monitor/commit/963234087288ee68d2480635bfc8ba1fdf9f5b0b))
* send silent notifications at night ([2770661](https://github.com/pashamed/dtek-monitor/commit/277066156bdb2eab0a4369d784b575678a4f36e8))
* store bot state in a separate artifacts branch ([69a8b35](https://github.com/pashamed/dtek-monitor/commit/69a8b3594aba6eee308e6d06dec6a500c445ab57))


### Bug Fixes

* add 0 fetch-depth in workflow, change texts ([9632340](https://github.com/pashamed/dtek-monitor/commit/963234087288ee68d2480635bfc8ba1fdf9f5b0b))
* add quick return if outage didn't detected ([c7ab908](https://github.com/pashamed/dtek-monitor/commit/c7ab9084a70351f94459c589e1d0dbbb0daa3d5d))
* changa schedule cron to every 30 minutes (as reserve trigger) ([f7bed73](https://github.com/pashamed/dtek-monitor/commit/f7bed73bb6dd585f5c6e3b6645adaab2fd335562))
* change default cron interval ([70bfea1](https://github.com/pashamed/dtek-monitor/commit/70bfea19fe02fbd90fa9ee149c1ea20aef9526f2))
* change isScheduled detection ([13c05b9](https://github.com/pashamed/dtek-monitor/commit/13c05b9cef64ed1c89848a6f9713e17df07dbcd1))
* check Telegram API response before saving the last message ([806865d](https://github.com/pashamed/dtek-monitor/commit/806865d7607f1406c817f013ddcf7fdaf8195fe3))
* correct the missing bot token error message ([1edcd7a](https://github.com/pashamed/dtek-monitor/commit/1edcd7aeb6be6fad1897e132588579ae1a17396d))
* exit with non-zero code on failure ([5bdc6cc](https://github.com/pashamed/dtek-monitor/commit/5bdc6ccd47bef37a221c145a73601ec1b7397465))
* fixes after review ([9632340](https://github.com/pashamed/dtek-monitor/commit/963234087288ee68d2480635bfc8ba1fdf9f5b0b))
* ignore "message is not modified" error ([be34814](https://github.com/pashamed/dtek-monitor/commit/be34814f4b1e37e0270f002c191953c05c971cd4))
* remove extra blank line from the message ([6191077](https://github.com/pashamed/dtek-monitor/commit/619107737fdf2e189e7f671ea02cf8bcdf2b75b1))
* save only date and message_id of last message ([9632340](https://github.com/pashamed/dtek-monitor/commit/963234087288ee68d2480635bfc8ba1fdf9f5b0b))
* show a clear error when the house is not found ([fb8d5fa](https://github.com/pashamed/dtek-monitor/commit/fb8d5fab55f1642ee6dbb92b212998a0db68f763))


### Code Refactoring

* improve message template ([a33a209](https://github.com/pashamed/dtek-monitor/commit/a33a20928c355310968ac2ffc2c1c4d917641a0e))
* remove dotenv ([a33a209](https://github.com/pashamed/dtek-monitor/commit/a33a20928c355310968ac2ffc2c1c4d917641a0e))
* rename monitor workflow ([9632340](https://github.com/pashamed/dtek-monitor/commit/963234087288ee68d2480635bfc8ba1fdf9f5b0b))
* separate constants ([a33a209](https://github.com/pashamed/dtek-monitor/commit/a33a20928c355310968ac2ffc2c1c4d917641a0e))
* separate helpers ([a33a209](https://github.com/pashamed/dtek-monitor/commit/a33a20928c355310968ac2ffc2c1c4d917641a0e))


### Documentation

* remove contribution section from readme ([bf120a8](https://github.com/pashamed/dtek-monitor/commit/bf120a83fdefc23635329c7625accb387b440fae))
* update readme ([a33a209](https://github.com/pashamed/dtek-monitor/commit/a33a20928c355310968ac2ffc2c1c4d917641a0e))
* update README for the new behavior and switch to informal address ([4bb25e8](https://github.com/pashamed/dtek-monitor/commit/4bb25e8a859e0f62e931dad6127da9adfda14518))


### Continuous Integration

* add job timeout ([bf120a8](https://github.com/pashamed/dtek-monitor/commit/bf120a83fdefc23635329c7625accb387b440fae))
* add release-please ([9c63c32](https://github.com/pashamed/dtek-monitor/commit/9c63c32b646a126b56ece9b8548e2d688416348c))
* bump actions/checkout and actions/setup-node to v7 ([bf120a8](https://github.com/pashamed/dtek-monitor/commit/bf120a83fdefc23635329c7625accb387b440fae))
* enable npm cache and switch to npm ci ([bf120a8](https://github.com/pashamed/dtek-monitor/commit/bf120a83fdefc23635329c7625accb387b440fae))
* keep scheduled workflow enabled via GitHub API ([ee37efe](https://github.com/pashamed/dtek-monitor/commit/ee37efe006de6884470ec4dace2f14b3bfa2566b))
* queue overlapping runs instead of cancelling them ([bf120a8](https://github.com/pashamed/dtek-monitor/commit/bf120a83fdefc23635329c7625accb387b440fae))
* replace PAT with GITHUB_TOKEN ([d1f0a86](https://github.com/pashamed/dtek-monitor/commit/d1f0a864971b97a829f2a69939dec976b8d26232))
* restrict GITHUB_TOKEN to read-only ([bf120a8](https://github.com/pashamed/dtek-monitor/commit/bf120a83fdefc23635329c7625accb387b440fae))
* use node version from .nvmrc, add secrets ([9632340](https://github.com/pashamed/dtek-monitor/commit/963234087288ee68d2480635bfc8ba1fdf9f5b0b))

## [3.0.0](https://github.com/pashamed/dtek-monitor/compare/v2.1.0...v3.0.0) (2026-10-06)


### ⚠ BREAKING CHANGES

* the PAT secret is no longer used. Remove it from the repository secrets and revoke the token.
* bot state moved from the artifacts/ folder in main to the artifacts branch. The branch is created on the first run with an outage, which sends a new message. Bot commits "chore: update artifacts" no longer land in main.
* when the outage time or reason changes during the day, the bot sends a new message (with a notification) as a reply to the previous one instead of editing a single message per day.

### Features

* extend silent night mode to 22:00-08:00 ([217d839](https://github.com/pashamed/dtek-monitor/commit/217d83989dbf88eb13b947d81a27e3852196fb76))
* replace timestamps emoji and show dates before times ([7efae33](https://github.com/pashamed/dtek-monitor/commit/7efae33aca9e6f9c97826e4a310700bc329183be))
* reply with a new message when the outage changes ([2313fb4](https://github.com/pashamed/dtek-monitor/commit/2313fb4d1a9ece8b34dcd5a0389a283001479904))
* retry getting info and sending notification ([4d7bea4](https://github.com/pashamed/dtek-monitor/commit/4d7bea48180738196f762208819d86fba77ee1ec))
* send silent notifications at night ([2770661](https://github.com/pashamed/dtek-monitor/commit/277066156bdb2eab0a4369d784b575678a4f36e8))
* store bot state in a separate artifacts branch ([69a8b35](https://github.com/pashamed/dtek-monitor/commit/69a8b3594aba6eee308e6d06dec6a500c445ab57))


### Bug Fixes

* check Telegram API response before saving the last message ([806865d](https://github.com/pashamed/dtek-monitor/commit/806865d7607f1406c817f013ddcf7fdaf8195fe3))
* correct the missing bot token error message ([1edcd7a](https://github.com/pashamed/dtek-monitor/commit/1edcd7aeb6be6fad1897e132588579ae1a17396d))
* exit with non-zero code on failure ([5bdc6cc](https://github.com/pashamed/dtek-monitor/commit/5bdc6ccd47bef37a221c145a73601ec1b7397465))
* ignore "message is not modified" error ([be34814](https://github.com/pashamed/dtek-monitor/commit/be34814f4b1e37e0270f002c191953c05c971cd4))
* remove extra blank line from the message ([6191077](https://github.com/pashamed/dtek-monitor/commit/619107737fdf2e189e7f671ea02cf8bcdf2b75b1))
* show a clear error when the house is not found ([fb8d5fa](https://github.com/pashamed/dtek-monitor/commit/fb8d5fab55f1642ee6dbb92b212998a0db68f763))


### Documentation

* update README for the new behavior and switch to informal address ([4bb25e8](https://github.com/pashamed/dtek-monitor/commit/4bb25e8a859e0f62e931dad6127da9adfda14518))


### Continuous Integration

* add release-please ([9c63c32](https://github.com/pashamed/dtek-monitor/commit/9c63c32b646a126b56ece9b8548e2d688416348c))
* keep scheduled workflow enabled via GitHub API ([ee37efe](https://github.com/pashamed/dtek-monitor/commit/ee37efe006de6884470ec4dace2f14b3bfa2566b))
* replace PAT with GITHUB_TOKEN ([d1f0a86](https://github.com/pashamed/dtek-monitor/commit/d1f0a864971b97a829f2a69939dec976b8d26232))

## [2.1.0](https://github.com/mr-devboy/dtek-monitor/compare/v2.0.0...v2.1.0) (2026-10-05)


### Features

* extend silent night mode to 22:00-08:00 ([217d839](https://github.com/mr-devboy/dtek-monitor/commit/217d83989dbf88eb13b947d81a27e3852196fb76))

## [2.0.0](https://github.com/mr-devboy/dtek-monitor/compare/v1.2.3...v2.0.0) (2026-10-05)


### ⚠ BREAKING CHANGES

* the PAT secret is no longer used. Remove it from the repository secrets and revoke the token.
* bot state moved from the artifacts/ folder in main to the artifacts branch. The branch is created on the first run with an outage, which sends a new message. Bot commits "chore: update artifacts" no longer land in main.
* when the outage time or reason changes during the day, the bot sends a new message (with a notification) as a reply to the previous one instead of editing a single message per day.

### Features

* replace timestamps emoji and show dates before times ([7efae33](https://github.com/mr-devboy/dtek-monitor/commit/7efae33aca9e6f9c97826e4a310700bc329183be))
* reply with a new message when the outage changes ([2313fb4](https://github.com/mr-devboy/dtek-monitor/commit/2313fb4d1a9ece8b34dcd5a0389a283001479904))
* retry getting info and sending notification ([4d7bea4](https://github.com/mr-devboy/dtek-monitor/commit/4d7bea48180738196f762208819d86fba77ee1ec))
* send silent notifications at night ([2770661](https://github.com/mr-devboy/dtek-monitor/commit/277066156bdb2eab0a4369d784b575678a4f36e8))
* store bot state in a separate artifacts branch ([69a8b35](https://github.com/mr-devboy/dtek-monitor/commit/69a8b3594aba6eee308e6d06dec6a500c445ab57))


### Bug Fixes

* check Telegram API response before saving the last message ([806865d](https://github.com/mr-devboy/dtek-monitor/commit/806865d7607f1406c817f013ddcf7fdaf8195fe3))
* correct the missing bot token error message ([1edcd7a](https://github.com/mr-devboy/dtek-monitor/commit/1edcd7aeb6be6fad1897e132588579ae1a17396d))
* exit with non-zero code on failure ([5bdc6cc](https://github.com/mr-devboy/dtek-monitor/commit/5bdc6ccd47bef37a221c145a73601ec1b7397465))
* ignore "message is not modified" error ([be34814](https://github.com/mr-devboy/dtek-monitor/commit/be34814f4b1e37e0270f002c191953c05c971cd4))
* remove extra blank line from the message ([6191077](https://github.com/mr-devboy/dtek-monitor/commit/619107737fdf2e189e7f671ea02cf8bcdf2b75b1))
* show a clear error when the house is not found ([fb8d5fa](https://github.com/mr-devboy/dtek-monitor/commit/fb8d5fab55f1642ee6dbb92b212998a0db68f763))


### Documentation

* update README for the new behavior and switch to informal address ([4bb25e8](https://github.com/mr-devboy/dtek-monitor/commit/4bb25e8a859e0f62e931dad6127da9adfda14518))


### Continuous Integration

* add release-please ([9c63c32](https://github.com/mr-devboy/dtek-monitor/commit/9c63c32b646a126b56ece9b8548e2d688416348c))
* keep scheduled workflow enabled via GitHub API ([ee37efe](https://github.com/mr-devboy/dtek-monitor/commit/ee37efe006de6884470ec4dace2f14b3bfa2566b))
* replace PAT with GITHUB_TOKEN ([d1f0a86](https://github.com/mr-devboy/dtek-monitor/commit/d1f0a864971b97a829f2a69939dec976b8d26232))
