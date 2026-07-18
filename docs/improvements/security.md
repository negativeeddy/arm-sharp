# Security — LogsController Sanitization

`LogsController.Reader` uses `Path.GetFileName` for sanitization but could use `Path.GetFullPath` + prefix validation as defense-in-depth.
