// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
//! Running as a Windows service: tell Windows it runs, stop when asked.

use std::ffi::OsString;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;
use std::time::Duration;
use windows_service::service::{ServiceControl, ServiceControlAccept, ServiceExitCode, ServiceState, ServiceStatus, ServiceType};
use windows_service::service_control_handler::{self, ServiceControlHandlerResult};
use windows_service::{define_windows_service, service_dispatcher};

pub const NAME: &str = "PCButlerSearch";

static USER: OnceLock<String> = OnceLock::new();
static STOP: AtomicBool = AtomicBool::new(false);

define_windows_service!(ffi_service_main, service_main);

pub fn start(sid: String) -> Result<(), String> {
    let _ = USER.set(sid);
    service_dispatcher::start(NAME, ffi_service_main).map_err(|error| error.to_string())
}

fn status(state: ServiceState, code: u32) -> ServiceStatus {
    ServiceStatus {
        service_type: ServiceType::OWN_PROCESS,
        current_state: state,
        controls_accepted: if state == ServiceState::Running { ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN } else { ServiceControlAccept::empty() },
        exit_code: ServiceExitCode::Win32(code),
        checkpoint: 0,
        wait_hint: Duration::from_secs(5),
        process_id: None,
    }
}

fn service_main(_arguments: Vec<OsString>) {
    let handler = |control| match control {
        ServiceControl::Stop | ServiceControl::Shutdown => {
            STOP.store(true, Ordering::SeqCst);
            crate::server::wake();
            ServiceControlHandlerResult::NoError
        }
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        _ => ServiceControlHandlerResult::NotImplemented,
    };
    let Ok(handle) = service_control_handler::register(NAME, handler) else {
        return;
    };
    let _ = handle.set_service_status(status(ServiceState::Running, 0));
    let sid = USER.get().cloned().unwrap_or_default();
    let code = match crate::server::run(&sid, &STOP) {
        Ok(()) => 0,
        Err(_) => 1,
    };
    let _ = handle.set_service_status(status(ServiceState::Stopped, code));
    // Connections still open end with the process.
    std::process::exit(0);
}
