"""Resolve public GitHub defaults once, before the portable request is validated."""
import contract
import preview


def resolve(text, variables, size_profile='request', action='validate'):
    request = contract.parse(text)
    preview.require(isinstance(request, dict), 'Expected a deployment request object.')
    preview.require(size_profile in ('request', '1', '2', '3', '4', '5'), 'Unknown Azure size profile.')
    if request.get('provider') != 'azure-vm' or action in ('status', 'destroy'):
        preview.require(size_profile == 'request', 'This operation requires the recorded request, without size-profile overrides.')
        return contract.validate(request)
    target = request.get('target')
    preview.require(isinstance(target, dict), 'Expected an Azure target object.')
    if size_profile != 'request':
        size = variables.get('AZURE_PREVIEW_VM_SIZE' + size_profile, '')
        preview.require(size in ('Standard_B2ms', 'Standard_B4ms'), 'The selected Azure size profile is empty or unsupported.')
        preview.require(target.get('vmSize', size) == size, 'The selected size profile conflicts with the explicit request size.')
        target.setdefault('vmSize', size)
    else:
        target.setdefault('vmSize', variables.get('AZURE_PREVIEW_VM_SIZE1', ''))
    target.setdefault('imageVersion', variables.get('AZURE_PREVIEW_IMAGE_VERSION', ''))
    target.setdefault('osProfile', variables.get('AZURE_PREVIEW_OS_PROFILE') or contract.AZURE_OS_PROFILE)
    return contract.validate(request)
