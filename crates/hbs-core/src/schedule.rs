//! The processing window: HandBrake only runs between two local times of day.

use serde::{Deserialize, Serialize};

/// Seconds since local midnight.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub struct Clock(pub u32);

impl Clock {
    pub fn hms(h: u32, m: u32, s: u32) -> Self {
        Self(h * 3600 + m * 60 + s)
    }
}

impl std::fmt::Display for Clock {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{:02}:{:02}", self.0 / 3600, (self.0 / 60) % 60)
    }
}

/// "22:00", "22:00:00", "6:05" → seconds of the day.
pub fn parse_clock(s: &str) -> Option<Clock> {
    let s = s.trim();
    if s.is_empty() {
        return None;
    }
    let mut parts = s.split(':');
    let h: u32 = parts.next()?.trim().parse().ok()?;
    let m: u32 = parts.next()?.trim().parse().ok()?;
    let sec: u32 = match parts.next() {
        Some(x) => x.trim().parse().ok()?,
        None => 0,
    };
    if parts.next().is_some() || h > 23 || m > 59 || sec > 59 {
        return None;
    }
    Some(Clock::hms(h, m, sec))
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct TimeWindow {
    pub start: Clock,
    pub end: Clock,
}

impl TimeWindow {
    /// Whether `now` is inside the window (a window ending before it starts spans midnight).
    pub fn contains(&self, now: Clock) -> bool {
        in_window(self.start, self.end, now)
    }

    /// Seconds until the window next opens (0 if it's open now).
    pub fn secs_until_open(&self, now: Clock) -> u32 {
        if self.contains(now) {
            0
        } else if now < self.start {
            self.start.0 - now.0
        } else {
            86_400 - now.0 + self.start.0
        }
    }
}

pub fn in_window(start: Clock, end: Clock, now: Clock) -> bool {
    if start <= end {
        now >= start && now <= end
    } else {
        now >= start || now <= end
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_clocks() {
        assert_eq!(parse_clock("22:00"), Some(Clock::hms(22, 0, 0)));
        assert_eq!(parse_clock("06:05:30"), Some(Clock::hms(6, 5, 30)));
        assert_eq!(parse_clock(" 6:5 "), Some(Clock::hms(6, 5, 0)));
        assert_eq!(parse_clock("24:00"), None);
        assert_eq!(parse_clock("abc"), None);
        assert_eq!(parse_clock(""), None);
        assert_eq!(Clock::hms(6, 5, 0).to_string(), "06:05");
    }

    #[test]
    fn windows_including_midnight() {
        let day = TimeWindow { start: Clock::hms(9, 0, 0), end: Clock::hms(17, 0, 0) };
        assert!(day.contains(Clock::hms(12, 0, 0)));
        assert!(!day.contains(Clock::hms(20, 0, 0)));
        let night = TimeWindow { start: Clock::hms(22, 0, 0), end: Clock::hms(6, 0, 0) };
        assert!(night.contains(Clock::hms(23, 0, 0)));
        assert!(night.contains(Clock::hms(3, 0, 0)));
        assert!(!night.contains(Clock::hms(12, 0, 0)));
        assert_eq!(night.secs_until_open(Clock::hms(21, 0, 0)), 3600);
        assert_eq!(night.secs_until_open(Clock::hms(23, 0, 0)), 0);
        assert_eq!(day.secs_until_open(Clock::hms(18, 0, 0)), 15 * 3600);
    }
}
