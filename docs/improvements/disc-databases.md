# Disc Databases — thediscdb.com Integration

Encrypted BDs often return 0 tracks from `makemkvcon info --robot`. thediscdb.com stores disc IDs mapped to known track layouts. Adding a lookup step would let us skip the expensive `makemkvcon info` scan for known discs and identify the correct main feature track without guessing by filesize.

API is simple REST — define a `DiscDatabaseService` client, cache results locally, and plug into `IdentifyService`.
