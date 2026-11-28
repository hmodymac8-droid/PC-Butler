// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
//! One drive opened for reading: raw reads at an offset, and the change journal's calls.
//! The volume is opened with GENERIC_READ only; nothing here can write to it.

use crate::ffi::*;
use std::ffi::c_void;
use std::mem::size_of;

pub struct Volume {
    handle: Handle,
    pub letter: u16,
}

/// What went wrong, as Windows said it.
#[derive(Debug, Clone, Copy)]
pub struct WinError(pub u32);

impl Volume {
    pub fn open(letter: u16) -> Result<Volume, WinError> {
        let path = format!("\\\\.\\{}:", char::from_u32(letter as u32).unwrap_or('C'));
        let name = wide(&path);
        let handle = unsafe {
            CreateFileW(name.as_ptr(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, std::ptr::null(),
                        OPEN_EXISTING, 0, std::ptr::null_mut())
        };
        let handle = Handle(handle);
        if !handle.valid() {
            return Err(WinError(last_error()));
        }
        Ok(Volume { handle, letter })
    }

    /// Read exactly buffer.len() bytes from this offset (both a multiple of the sector size).
    pub fn read_at(&self, offset: u64, buffer: &mut [u8]) -> Result<(), WinError> {
        unsafe {
            if SetFilePointerEx(self.handle.0, offset as i64, std::ptr::null_mut(), FILE_BEGIN) == 0 {
                return Err(WinError(last_error()));
            }
        }
        let mut done = 0usize;
        while done < buffer.len() {
            let mut got = 0u32;
            let want = (buffer.len() - done).min(64 << 20) as u32;
            let ok = unsafe { ReadFile(self.handle.0, buffer[done..].as_mut_ptr() as *mut c_void, want, &mut got, std::ptr::null_mut()) };
            if ok == 0 {
                return Err(WinError(last_error()));
            }
            if got == 0 {
                return Err(WinError(ERROR_HANDLE_EOF));
            }
            done += got as usize;
        }
        Ok(())
    }

    fn control(&self, code: u32, input: *const c_void, input_size: usize, output: &mut [u8]) -> Result<usize, WinError> {
        let mut returned = 0u32;
        let ok = unsafe {
            DeviceIoControl(self.handle.0, code, input, input_size as u32, output.as_mut_ptr() as *mut c_void, output.len() as u32,
                            &mut returned, std::ptr::null_mut())
        };
        if ok == 0 {
            return Err(WinError(last_error()));
        }
        Ok(returned as usize)
    }

    /// The change journal's identity and positions; an error when the drive has none.
    pub fn journal(&self) -> Result<USN_JOURNAL_DATA_V0, WinError> {
        let mut out = [0u8; 80];
        let got = self.control(FSCTL_QUERY_USN_JOURNAL, std::ptr::null(), 0, &mut out)?;
        if got < size_of::<USN_JOURNAL_DATA_V0>() {
            return Err(WinError(ERROR_JOURNAL_NOT_ACTIVE));
        }
        Ok(unsafe { std::ptr::read_unaligned(out.as_ptr() as *const USN_JOURNAL_DATA_V0) })
    }

    /// Journal records from start on, without waiting: the first 8 bytes of output are the next position.
    pub fn read_journal(&self, journal: u64, start: i64, output: &mut [u8]) -> Result<usize, WinError> {
        let input = READ_USN_JOURNAL_DATA_V0 {
            StartUsn: start, ReasonMask: 0xFFFF_FFFF, ReturnOnlyOnClose: 0, Timeout: 0, BytesToWaitFor: 0, UsnJournalID: journal,
        };
        self.control(FSCTL_READ_USN_JOURNAL, &input as *const _ as *const c_void, size_of::<READ_USN_JOURNAL_DATA_V0>(), output)
    }

    /// Every file record, through the journal's enumeration (the slower way, used when $MFT cannot be read).
    pub fn enumerate(&self, start: u64, high: i64, output: &mut [u8]) -> Result<usize, WinError> {
        let input = MFT_ENUM_DATA_V0 { StartFileReferenceNumber: start, LowUsn: 0, HighUsn: high };
        self.control(FSCTL_ENUM_USN_DATA, &input as *const _ as *const c_void, size_of::<MFT_ENUM_DATA_V0>(), output)
    }
}

/// True for a fixed drive formatted NTFS: the only kind the service reads.
pub fn is_fixed_ntfs(letter: u16) -> bool {
    let root = wide(&format!("{}:\\", char::from_u32(letter as u32).unwrap_or('?')));
    unsafe {
        if GetDriveTypeW(root.as_ptr()) != DRIVE_FIXED {
            return false;
        }
        let mut fs = [0u16; 32];
        if GetVolumeInformationW(root.as_ptr(), std::ptr::null_mut(), 0, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut(),
                                 fs.as_mut_ptr(), fs.len() as u32) == 0 {
            return false;
        }
        let end = fs.iter().position(|&c| c == 0).unwrap_or(fs.len());
        String::from_utf16_lossy(&fs[..end]).eq_ignore_ascii_case("NTFS")
    }
}

/// Little-endian readers over a byte slice; out of range reads give 0, so a damaged record cannot crash the service.
pub fn u16_at(bytes: &[u8], at: usize) -> u16 {
    bytes.get(at..at + 2).map_or(0, |b| u16::from_le_bytes([b[0], b[1]]))
}

pub fn u32_at(bytes: &[u8], at: usize) -> u32 {
    bytes.get(at..at + 4).map_or(0, |b| u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
}

pub fn u64_at(bytes: &[u8], at: usize) -> u64 {
    bytes.get(at..at + 8).map_or(0, |b| u64::from_le_bytes([b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7]]))
}

/// UTF-16 text of count characters at a byte offset.
pub fn utf16_at(bytes: &[u8], at: usize, count: usize) -> Option<Vec<u16>> {
    let raw = bytes.get(at..at + count * 2)?;
    Some(raw.chunks_exact(2).map(|pair| u16::from_le_bytes([pair[0], pair[1]])).collect())
}
