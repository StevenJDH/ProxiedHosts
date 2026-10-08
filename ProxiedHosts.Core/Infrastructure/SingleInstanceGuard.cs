
/*
 * This file is part of ProxiedHosts <https://github.com/StevenJDH/ProxiedHosts>.
 * Copyright (C) 2026 Steven Jenkins De Haro.
 *
 * ProxiedHosts is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProxiedHosts is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProxiedHosts.  If not, see <http://www.gnu.org/licenses/>.
 */

namespace ProxiedHosts.Core.Infrastructure;

/// <summary>
/// Enforces a single application instance across executables by holding an
/// exclusive lock on a shared file in the application data directory.
/// The lock is automatically released when the file stream is disposed or
/// the process terminates. Unlike a named mutex, file locking is not tied
/// to thread ownership, making it suitable for asynchronous applications
/// where execution may resume on different threads.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream _lockFile;

    private SingleInstanceGuard(FileStream lockFile)
    {
        _lockFile = lockFile;
    }

    public static SingleInstanceGuard? TryAcquire()
    {
        var lockFilePath = Path.Combine(ApplicationPaths.GetDataDirectory(), "instance.lock");

        try
        {
            var stream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            return new SingleInstanceGuard(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _lockFile.Dispose();
    }
}