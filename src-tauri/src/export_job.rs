use std::{
    fs,
    io::{Read, Write},
    path::Path,
    process::{Command, Output, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread,
    time::Duration,
};

pub const CANCELLED: &str = "Export stopped";

#[derive(Default)]
pub struct ExportJobs(Arc<Mutex<Option<ActiveJob>>>);

struct ActiveJob {
    id: String,
    claimed: bool,
    control: Arc<ExportControl>,
}

#[derive(Default)]
pub struct ExportControl {
    cancelled: AtomicBool,
    publication: Mutex<()>,
}

pub struct ExportJob {
    jobs: Arc<Mutex<Option<ActiveJob>>>,
    pub control: Arc<ExportControl>,
}

impl ExportJobs {
    pub fn reserve(&self) -> Result<String, String> {
        let mut active = self.0.lock().map_err(|error| error.to_string())?;
        if active.is_some() {
            return Err("An export is already running.".into());
        }
        let id = uuid::Uuid::new_v4().to_string();
        *active = Some(ActiveJob {
            id: id.clone(),
            claimed: false,
            control: Arc::new(ExportControl::default()),
        });
        Ok(id)
    }

    pub fn claim(&self, id: &str) -> Result<ExportJob, String> {
        let mut active = self.0.lock().map_err(|error| error.to_string())?;
        let job = active
            .as_mut()
            .filter(|job| job.id == id && !job.claimed)
            .ok_or("Export job is no longer available.")?;
        job.claimed = true;
        Ok(ExportJob {
            jobs: self.0.clone(),
            control: job.control.clone(),
        })
    }

    pub fn cancel(&self, id: &str) -> Result<bool, String> {
        let active = self.0.lock().map_err(|error| error.to_string())?;
        if let Some(job) = active.as_ref().filter(|job| job.id == id) {
            job.control.cancel();
            Ok(true)
        } else {
            Ok(false)
        }
    }
}

impl Drop for ExportJobs {
    fn drop(&mut self) {
        if let Ok(active) = self.0.lock() {
            if let Some(job) = active.as_ref() {
                job.control.cancel();
            }
        }
    }
}

impl Drop for ExportJob {
    fn drop(&mut self) {
        if let Ok(mut active) = self.jobs.lock() {
            *active = None;
        }
    }
}

impl ExportControl {
    pub fn cancel(&self) {
        let _publication = self
            .publication
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        self.cancelled.store(true, Ordering::SeqCst);
    }

    pub fn publish(&self, source: &Path, destination: &Path) -> Result<(), String> {
        let _publication = self.publication.lock().map_err(|error| error.to_string())?;
        self.check()?;
        fs::rename(source, destination).map_err(|error| error.to_string())
    }

    pub fn check(&self) -> Result<(), String> {
        if self.cancelled.load(Ordering::SeqCst) {
            Err(CANCELLED.into())
        } else {
            Ok(())
        }
    }

    // Drain both pipes while polling so FFmpeg cannot block on a full stderr pipe.
    pub fn output(&self, command: &mut Command, label: &str) -> Result<Output, String> {
        self.check()?;
        let mut child = command
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|error| format!("{label}: {error}"))?;
        let mut stdout = child.stdout.take().ok_or("Missing process stdout")?;
        let mut stderr = child.stderr.take().ok_or("Missing process stderr")?;
        thread::scope(|scope| {
            let out = scope.spawn(move || {
                let mut bytes = Vec::new();
                stdout.read_to_end(&mut bytes).map(|_| bytes)
            });
            let err = scope.spawn(move || {
                let mut bytes = Vec::new();
                stderr.read_to_end(&mut bytes).map(|_| bytes)
            });
            let status = loop {
                if let Err(error) = self.check() {
                    let _ = child.kill();
                    let _ = child.wait();
                    break Err(error);
                }
                match child.try_wait() {
                    Ok(Some(status)) => break Ok(status),
                    Ok(None) => thread::sleep(Duration::from_millis(25)),
                    Err(error) => {
                        let _ = child.kill();
                        let _ = child.wait();
                        break Err(format!("{label}: {error}"));
                    }
                }
            };
            let stdout = out
                .join()
                .map_err(|_| "Could not read process stdout".to_string())?;
            let stderr = err
                .join()
                .map_err(|_| "Could not read process stderr".to_string())?;
            let status = status?;
            self.check()?;
            Ok(Output {
                status,
                stdout: stdout.map_err(|error| error.to_string())?,
                stderr: stderr.map_err(|error| error.to_string())?,
            })
        })
    }

    pub fn run(&self, exe: &str, args: &[&str], label: &str) -> Result<(), String> {
        let output = self.output(super::hidden_command(exe).args(args), label)?;
        if output.status.success() {
            Ok(())
        } else {
            Err(super::command_error_message(
                label,
                &String::from_utf8_lossy(&output.stderr),
            ))
        }
    }

    pub fn copy(&self, source: &Path, destination: &Path) -> Result<u64, String> {
        self.check()?;
        let mut source = fs::File::open(source).map_err(|error| error.to_string())?;
        let mut destination = fs::File::create(destination).map_err(|error| error.to_string())?;
        let mut buffer = vec![0; 1024 * 1024];
        let mut total = 0;
        loop {
            self.check()?;
            let count = source
                .read(&mut buffer)
                .map_err(|error| error.to_string())?;
            if count == 0 {
                return Ok(total);
            }
            destination
                .write_all(&buffer[..count])
                .map_err(|error| error.to_string())?;
            total += count as u64;
        }
    }
}
