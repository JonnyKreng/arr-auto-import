## Update pipeline

- Load all manual import options 
- Get all track names from the album
- If the deterministic checks fall throught, ask laya
- First queststion: Is the downloaded album the same we want to import
  - Add album name of the album we want to import
  - Add the inforamtion of the downloaded album/track
  - If that is not the case blocklist the album
- Secound Question for each track: Map the album track to the download rack
  - List all downloaed tracks ask laya to map it to the album track
  - Submit all applied tracks, discard the rest.