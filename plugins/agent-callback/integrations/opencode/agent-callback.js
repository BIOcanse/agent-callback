import { spawn } from "node:child_process"

const agentCallbackExecutable = __AGENT_CALLBACK_EXECUTABLE_JSON__
const managedInstallId = __AGENT_CALLBACK_INSTALL_ID_JSON__

function registerConnection(serverUrl) {
  const payload = JSON.stringify({
    serverUrl,
    username: process.env.OPENCODE_SERVER_USERNAME || "opencode",
    password: process.env.OPENCODE_SERVER_PASSWORD || "",
    source: "opencode-plugin",
    processId: process.pid,
  })

  return new Promise((resolve, reject) => {
    const child = spawn(
      agentCallbackExecutable,
      ["provider", "connect", "opencode", "--stdin"],
      {
        windowsHide: true,
        stdio: ["pipe", "pipe", "pipe"],
      },
    )
    let stdout = ""
    let stderr = ""

    child.stdout.setEncoding("utf8")
    child.stderr.setEncoding("utf8")
    child.stdout.on("data", (chunk) => {
      stdout = (stdout + chunk).slice(-65536)
    })
    child.stderr.on("data", (chunk) => {
      stderr = (stderr + chunk).slice(-4096)
    })
    child.on("error", reject)
    child.on("close", (code) => {
      if (code !== 0) {
        reject(new Error(stderr.trim() || "Agent Callback exited with code " + code))
        return
      }

      try {
        const result = JSON.parse(stdout)
        if (!result.connectionId) {
          throw new Error("Agent Callback did not return an OpenCode connection ID")
        }

        resolve(result)
      } catch (error) {
        reject(error)
      }
    })
    child.stdin.end(payload, "utf8")
  })
}

export const AgentCallbackPlugin = async ({ serverUrl }) => {
  const normalizedServerUrl = String(serverUrl).replace(/\/$/, "")
  let connection = registerConnection(normalizedServerUrl).catch(() => null)

  async function requireConnection() {
    let result = await connection
    if (!result) {
      connection = registerConnection(normalizedServerUrl).catch(() => null)
      result = await connection
    }

    if (!result) {
      throw new Error(
        "Agent Callback OpenCode integration " +
          managedInstallId +
          " could not reach its local Host",
      )
    }

    return result
  }

  return {
    "shell.env": async (input, output) => {
      const result = await requireConnection()
      output.env.AGENT_CALLBACK_PROVIDER = "opencode"
      output.env.AGENT_CALLBACK_TARGET_ID =
        result.connectionId + ":" + input.sessionID
    },
  }
}
