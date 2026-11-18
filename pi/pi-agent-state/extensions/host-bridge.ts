// host-bridge: lets pi run commands on the HOST machine (outside the container).
// Transport: ctx.ui dialogs forwarded over pi's RPC extension-UI protocol; the API
// client answers `host-bridge` editor envelopes by executing the command on the host
// and returning the JSON result as the dialog value. See pi-rpc-api design doc.
import { Type } from "typebox";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

export default function (pi: ExtensionAPI) {
	// Dialog value = the host command result (JSON string) sent by the API client.
	async function roundTrip(command: string, ctx: any): Promise<string> {
		const answer: string | undefined = await ctx.ui.editor("host-bridge", JSON.stringify({ command }));
		if (!answer) throw new Error("host-bridge unavailable (client did not answer)");
		return answer;
	}

	pi.registerTool({
		name: "host_shell",
		description:
			"Run a shell command on the HOST machine (outside this container). " +
			"Use for host-only resources: host files outside /workspace, services on host localhost, host-only CLIs. " +
			'Returns JSON {"exitCode","stdout","stderr"}.',
		parameters: Type.Object({ command: Type.String() }),
		async execute(_id, params, _signal, _onUpdate, ctx) {
			try {
				const answer = await roundTrip(params.command, ctx);
				return { content: [{ type: "text", text: answer }] };
			} catch (err) {
				return { content: [{ type: "text", text: JSON.stringify({ error: String(err) }) }] };
			}
		},
	});

	pi.registerCommand("host-check", {
		description: "Round trip a host command via host-bridge (smoke check)",
		handler: async (_args, ctx) => {
			try {
				const answer = await roundTrip("host-bridge check", ctx);
				pi.sendMessage({ customType: "host-bridge-check", content: answer, display: true });
				ctx.ui.notify(`host-bridge: ${answer}`, answer.includes('"exitCode":0') ? "info" : "warning");
			} catch (err) {
				ctx.ui.notify(`host-bridge check failed: ${err}`, "error");
			}
		},
	});
}
