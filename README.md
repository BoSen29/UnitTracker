# UnitTracker for Legion TD 2

Forked from LogLess https://github.com/LegionTD2-Modding/Logless

Tracks the builds ingame, and posts the data to a specified url with the specified JWT.

## Installation (Instructions stolen from https://github.com/LegionTD2-Modding/NarrowMasterMinded/ )
- Close the game
- If not already done, follow this guide to install [BepInEx](https://github.com/LegionTD2-Modding/.github/wiki/Installation-of-BepInEx)
- Download the latest [release](https://github.com/BoSen29/UnitTracker/releases/latest), and drop `UnitTracker.dll` inside your `Legion TD 2/BepInEx/plugins/` folder
- You are done, you can start the game and enjoy!

## Configuration

DM @bosen29 on Discord for a juicy JWT to identify your requests, and install the overlay in your stream (currently invite only).
After first launching the game a configuration file will be created at `Legion TD 2/BepInEx/config/UnitTracker.cfg`, open the config-file for editing and paste the JWT after the JWT configuration option, and optionally the stream-delay for your stream.

Note: the Overlay in Twitch needs to be configured to match the identity provided by BoSen. BoSen will provide this information along with the JWT upon inqueries.

## Upgrading 

Any non-breaking upgrades should just be a matter of copy and replace the current UnitTracker.dll with a new one. Any breaking changes requiring a different approach will be documented with the release.

## Changelog
#### 1.1.17 
Fixed issues with wavenumbers when specating a game in progress. Credits to Nyctea for help in testing this one.
#### 1.1.6
Fixed fetching data during spectate sessions. 
#### 1.1.5 
Fixed the data fetching issues.
#### 1.1.4 
Another attempted fix at solving the issue of missing data from scoreboard. Now with forced exceptions @ 0.
#### 1.1.3 
Fixed issues fetching Scoreboard-data during high-density mercenary spawning on west side.
