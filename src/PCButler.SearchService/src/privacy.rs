// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
//! Who the service works for, and what of the PC it does not tell them: the profile folders
//! of the PC's other accounts (C:\Users\someone-else). Only their own profile and Public pass.

use crate::ffi::*;
use std::ffi::c_void;

#[derive(Clone)]
pub struct Privacy {
    /// The drive the profiles are on, as an upper-case letter.
    letter: u16,
    /// The profiles' folder below the drive's top, in lower case: ["users"].
    parts: Vec<String>,
    /// "c:\users\"
    prefix: String,
    /// The user's own profile folder name, in lower case ("" when it could not be found).
    me: String,
}

impl Privacy {
    /// From the user's profile folder, such as C:\Users\Name.
    pub fn from_profile(profile: &str) -> Privacy {
        let profile = profile.trim_end_matches('\\');
        let (folder, me) = match profile.rfind('\\') {
            Some(at) => (&profile[..at], &profile[at + 1..]),
            None => (profile, ""),
        };
        Privacy::new(folder, me)
    }

    fn new(folder: &str, me: &str) -> Privacy {
        let lower = folder.to_lowercase();
        let letter = folder.chars().next().map_or(b'C' as u16, |c| c.to_ascii_uppercase() as u16);
        let parts = lower.split('\\').skip(1).filter(|part| !part.is_empty()).map(str::to_string).collect();
        Privacy { letter, parts, prefix: format!("{}\\", lower), me: me.to_lowercase() }
    }

    /// For a user whose profile is unknown: the system drive's Users folder, with only Public shown.
    fn unknown() -> Privacy {
        let drive = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".to_string());
        Privacy::new(&format!("{}\\Users", drive), "")
    }

    /// The profile of the user with this SID, from Windows' list of profiles.
    pub fn for_sid(sid: &str) -> Privacy {
        match profile_of(sid) {
            Some(profile) => Privacy::from_profile(&profile),
            None => Privacy::unknown(),
        }
    }

    /// The profiles' folder below the top of this drive, or None on another drive.
    pub fn parts_on(&self, letter: u16) -> Option<&[String]> {
        (letter == self.letter).then_some(&self.parts[..])
    }

    /// A folder directly in the profiles' folder that may be shown.
    pub fn allowed_profile(&self, name: &[u16]) -> bool {
        let name = String::from_utf16_lossy(name).to_lowercase();
        name == "public" || (!self.me.is_empty() && name == self.me)
    }

    /// A full path that may be shown (not inside another user's profile).
    pub fn allows(&self, path: &[u16]) -> bool {
        let lower = String::from_utf16_lossy(path).to_lowercase();
        match lower.strip_prefix(&self.prefix) {
            Some(rest) => {
                let first = rest.split('\\').next().unwrap_or("");
                first == "public" || (!self.me.is_empty() && first == self.me)
            }
            None => true,
        }
    }
}

fn read_string(key: &str, value: &str) -> Option<String> {
    let key = wide(key);
    let value = wide(value);
    let mut buffer = vec![0u16; 1024];
    let mut size = (buffer.len() * 2) as u32;
    let status = unsafe {
        RegGetValueW(HKEY_LOCAL_MACHINE, key.as_ptr(), value.as_ptr(), RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND,
                     std::ptr::null_mut(), buffer.as_mut_ptr() as *mut c_void, &mut size)
    };
    if status != 0 {
        return None;
    }
    let end = buffer.iter().position(|&c| c == 0).unwrap_or(buffer.len());
    let raw = wide(&String::from_utf16_lossy(&buffer[..end]));
    let mut expanded = vec![0u16; 2048];
    let got = unsafe { ExpandEnvironmentStringsW(raw.as_ptr(), expanded.as_mut_ptr(), expanded.len() as u32) } as usize;
    if got == 0 || got > expanded.len() {
        return None;
    }
    let end = expanded.iter().position(|&c| c == 0).unwrap_or(expanded.len());
    Some(String::from_utf16_lossy(&expanded[..end]))
}

fn profile_of(sid: &str) -> Option<String> {
    if !sid.starts_with("S-1-") || sid.contains('\\') {
        return None;
    }
    read_string(&format!("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList\\{}", sid), "ProfileImagePath")
}

/// The SID of the account this program runs as (for --console, where the user is whoever started it).
pub fn current_sid() -> Option<String> {
    unsafe {
        let mut token: HANDLE = std::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return None;
        }
        let token = Handle(token);
        let mut buffer = vec![0u8; 256];
        let mut size = 0u32;
        if GetTokenInformation(token.0, TOKEN_USER_CLASS, buffer.as_mut_ptr() as *mut c_void, buffer.len() as u32, &mut size) == 0 {
            return None;
        }
        // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the SID's address.
        let sid = std::ptr::read_unaligned(buffer.as_ptr() as *const *mut c_void);
        let mut text: *mut u16 = std::ptr::null_mut();
        if ConvertSidToStringSidW(sid, &mut text) == 0 || text.is_null() {
            return None;
        }
        let mut length = 0;
        while *text.add(length) != 0 {
            length += 1;
        }
        let result = String::from_utf16_lossy(std::slice::from_raw_parts(text, length));
        LocalFree(text as *mut c_void);
        Some(result)
    }
}
